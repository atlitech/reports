using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Aspire.Hosting.Tests.Support;

/// <summary>
/// Builds application models and reads what the integration put in them, without running anything.
/// </summary>
internal static class AppModel
{
  /// <summary>
  /// The version of the hosting package, which the release also tags the server image with.
  /// </summary>
  internal static string PackageVersion { get; } =
    typeof(ReportsServerResource)
      .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
      .InformationalVersion.Split('+')[0];

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  internal static IDistributedApplicationBuilder CreateBuilder() =>
    DistributedApplication.CreateBuilder([]);

  /// <summary>
  /// A builder for <c>aspire publish</c>, which adds deployment environments to the model.
  /// </summary>
  internal static IDistributedApplicationBuilder CreatePublishBuilder() =>
    DistributedApplication.CreateBuilder(["--operation", "publish"]);

  /// <summary>
  /// Builds the app and raises <see cref="BeforeStartEvent"/>, after which the model is complete and
  /// publishers read it.
  /// </summary>
  internal static async Task RaiseBeforeStartAsync(IDistributedApplicationBuilder builder)
  {
    await using var app = builder.Build();
    await app
      .Services.GetRequiredService<IDistributedApplicationEventing>()
      .PublishAsync(
        new BeforeStartEvent(
          app.Services,
          app.Services.GetRequiredService<DistributedApplicationModel>()
        ),
        TestToken
      );
  }

  /// <summary>
  /// The arguments the resource adds to the container runtime's <c>run</c> command when the app runs.
  /// </summary>
  internal static async Task<IReadOnlyList<string>> ContainerRuntimeArgumentsAsync(
    IResource resource
  )
  {
    List<object> arguments = [];
    ContainerRuntimeArgsCallbackContext context = new(arguments, TestToken);
    foreach (
      var annotation in resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>()
    )
    {
      await annotation.Callback(context);
    }

    return [.. arguments.Select(argument => (string)argument)];
  }

  /// <summary>
  /// Gives an endpoint the address a running app would, so expressions over it resolve.
  /// </summary>
  internal static void Allocate(IResource resource, string endpointName, int port)
  {
    var endpoint = resource
      .Annotations.OfType<EndpointAnnotation>()
      .Single(annotation => annotation.Name == endpointName);
    endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", port);
  }

  /// <summary>
  /// The environment variables <paramref name="configure"/> adds to the resource, resolved as a
  /// running app (or, for <see cref="DistributedApplicationOperation.Publish"/>, a publisher) would
  /// resolve them.
  /// </summary>
  internal static Task<Dictionary<string, string?>> EnvironmentAddedByAsync<T>(
    IResourceBuilder<T> builder,
    Action<IResourceBuilder<T>> configure,
    DistributedApplicationOperation operation = DistributedApplicationOperation.Run
  )
    where T : IResourceWithEnvironment
  {
    var before = builder.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().ToHashSet();
    configure(builder);

    return EnvironmentAsync(
      builder.Resource,
      operation,
      annotation => !before.Contains(annotation)
    );
  }

  /// <summary>
  /// All the environment variables of the resource, resolved as a publisher would resolve them.
  /// </summary>
  /// <remarks>
  /// Publishing skips the callbacks that need a running app, such as the OTLP exporter's.
  /// </remarks>
  internal static Task<Dictionary<string, string?>> PublishedEnvironmentAsync(IResource resource) =>
    EnvironmentAsync(resource, DistributedApplicationOperation.Publish, _ => true);

  private static async Task<Dictionary<string, string?>> EnvironmentAsync(
    IResource resource,
    DistributedApplicationOperation operation,
    Func<EnvironmentCallbackAnnotation, bool> include
  )
  {
    EnvironmentCallbackContext context = new(
      new DistributedApplicationExecutionContext(operation),
      resource,
      cancellationToken: TestToken
    );
    foreach (
      var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Where(include)
    )
    {
      await annotation.Callback(context);
    }

    Dictionary<string, string?> values = [];
    foreach (var (name, value) in context.EnvironmentVariables)
    {
      values[name] = value switch
      {
        string text => text,
        IValueProvider provider when operation == DistributedApplicationOperation.Run =>
          await provider.GetValueAsync(TestToken),
        IManifestExpressionProvider expression => expression.ValueExpression,
        _ => value.ToString(),
      };
    }

    return values;
  }
}
