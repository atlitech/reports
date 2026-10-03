using System.Collections.Immutable;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Atli.Reports.Aspire.Hosting.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Aspire.Hosting.Tests;

/// <summary>
/// What <c>AddReportsServer</c> puts in the application model.
/// </summary>
public class AddReportsServerTests
{
  [Test]
  public async Task Runs_the_image_released_with_the_package()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    var image = server.Resource.Annotations.OfType<ContainerImageAnnotation>().Single();
    await Assert.That(image.Registry).IsEqualTo("ghcr.io");
    await Assert.That(image.Image).IsEqualTo("atlitech/reports-server");
    await Assert.That(image.Tag).IsEqualTo(AppModel.PackageVersion);
    await Assert.That(image.SHA256).IsNull();
  }

  [Test]
  public async Task The_package_version_is_a_release_version()
  {
    // The tag only matches a published image when it is the version the release builds.
    await Assert.That(AppModel.PackageVersion).Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");
  }

  [Test]
  public async Task The_http_endpoint_targets_the_server_port()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    var endpoint = server.Resource.Annotations.OfType<EndpointAnnotation>().Single();
    await Assert.That(endpoint.Name).IsEqualTo("http");
    await Assert.That(endpoint.UriScheme).IsEqualTo("http");
    await Assert.That(endpoint.TargetPort).IsEqualTo(8080);
    await Assert.That(endpoint.Port).IsNull();
    await Assert.That(endpoint.IsExternal).IsFalse();
    await Assert.That(server.Resource.PrimaryEndpoint.EndpointName).IsEqualTo("http");
  }

  [Test]
  public async Task The_port_argument_fixes_the_host_port()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server", port: 5150);

    var endpoint = server.Resource.Annotations.OfType<EndpointAnnotation>().Single();
    await Assert.That(endpoint.Port).IsEqualTo(5150);
    await Assert.That(endpoint.TargetPort).IsEqualTo(8080);
  }

  [Test]
  public async Task The_resource_is_healthy_once_the_server_is_ready()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    // WithHttpHealthCheck names its check after the resource, the endpoint, the path, and the status.
    var healthCheck = server.Resource.Annotations.OfType<HealthCheckAnnotation>().Single();
    await Assert.That(healthCheck.Key).IsEqualTo("reports-server_http_/health/ready_200_check");
  }

  [Test]
  public async Task Exports_telemetry_over_otlp()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    await Assert.That(server.Resource.Annotations.OfType<OtlpExporterAnnotation>()).HasSingleItem();
  }

  [Test]
  public async Task Leaves_the_engine_settings_to_the_image()
  {
    // The image's appsettings.json holds the server's defaults; the integration only sets what the
    // AppHost asks for.
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    var environment = await AppModel.PublishedEnvironmentAsync(server.Resource);

    await Assert
      .That(
        environment.Keys.Where(name => name.StartsWith("ReportsEngine__", StringComparison.Ordinal))
      )
      .IsEmpty();
  }

  [Test]
  public async Task Runs_the_container_with_the_seccomp_profile_Chromiums_sandbox_needs()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    var arguments = await AppModel.ContainerRuntimeArgumentsAsync(server.Resource);

    await Assert.That(arguments.Count).IsEqualTo(2);
    await Assert.That(arguments[0]).IsEqualTo("--security-opt");
    await Assert.That(arguments[1]).StartsWith("seccomp=");
    var path = arguments[1]["seccomp=".Length..];
    await Assert.That(Path.IsPathRooted(path)).IsTrue();
    var passed = await File.ReadAllBytesAsync(path);
    var repository = await RepositoryProfileAsync();
    await Assert.That(passed.SequenceEqual(repository)).IsTrue();
  }

  [Test]
  public async Task Each_start_passes_the_same_profile_file()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    var first = await AppModel.ContainerRuntimeArgumentsAsync(server.Resource);
    var second = await AppModel.ContainerRuntimeArgumentsAsync(server.Resource);

    // Named by its content, so a later package's profile gets a file of its own.
    await Assert.That(second).IsEquivalentTo(first);
    await Assert.That(Path.GetFileName(first[1])).Matches("^chromium-seccomp-[0-9a-f]{16}\\.json$");
  }

  [Test]
  public async Task Links_the_openapi_document_in_the_dashboard()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    ResourceUrlsCallbackContext context = new(
      new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
      server.Resource,
      cancellationToken: TestContext.Current!.Execution.CancellationToken
    );
    foreach (var annotation in server.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>())
    {
      await annotation.Callback(context);
    }

    // Relative to the endpoint, so the dashboard links the address the server actually got.
    var url = context.Urls.Single(link => link.DisplayText == "OpenAPI document");
    await Assert.That(url.Url).IsEqualTo("/openapi/v1.json");
    await Assert.That(url.Endpoint?.EndpointName).IsEqualTo("http");
  }

  [Test]
  public async Task Offers_a_test_page_command_while_the_server_is_healthy()
  {
    var server = AppModel
      .CreateBuilder()
      .AddReportsServer("reports-server")
      .WithDevelopmentApiKey();

    var command = server
      .Resource.Annotations.OfType<ResourceCommandAnnotation>()
      .Single(annotation => annotation.Name == "convert-test-page");
    await Assert.That(command.DisplayName).IsEqualTo("Convert a test page");
    await Assert.That(command.IconName).IsEqualTo("DocumentPdf");

    await using var services = new ServiceCollection().BuildServiceProvider();
    await Assert
      .That(command.UpdateState(StateContext(KnownResourceStates.Running, services)))
      .IsEqualTo(ResourceCommandState.Enabled);
    await Assert
      .That(command.UpdateState(StateContext(KnownResourceStates.Starting, services)))
      .IsEqualTo(ResourceCommandState.Disabled);
  }

  [Test]
  public async Task Builds_from_source_with_WithDockerfile()
  {
    var builder = AppModel.CreateBuilder();

    var server = builder
      .AddReportsServer("reports-server")
      .WithDockerfile("../reports", "src/Atli.Reports.Server/Dockerfile");

    var context = Path.GetFullPath("../reports", builder.AppHostDirectory);
    var build = server.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
    await Assert.That(build.ContextPath).IsEqualTo(context);
    await Assert
      .That(build.DockerfilePath)
      .IsEqualTo(Path.GetFullPath("src/Atli.Reports.Server/Dockerfile", context));

    // The built image replaces the released one; the endpoint, health check, and telemetry stay.
    await Assert.That(server.Resource.TryGetContainerImageName(out var image)).IsTrue();
    await Assert.That(image).DoesNotStartWith("ghcr.io/");
    await Assert.That(server.Resource.Annotations.OfType<EndpointAnnotation>()).HasSingleItem();
    await Assert.That(server.Resource.Annotations.OfType<HealthCheckAnnotation>()).HasSingleItem();
    await Assert.That(server.Resource.Annotations.OfType<OtlpExporterAnnotation>()).HasSingleItem();
  }

  [Test]
  public async Task WithImageTag_runs_another_release()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server").WithImageTag("1.2.3");

    await Assert.That(server.Resource.TryGetContainerImageName(out var image)).IsTrue();
    await Assert.That(image).IsEqualTo("ghcr.io/atlitech/reports-server:1.2.3");
  }

  [Test]
  [Arguments("")]
  [Arguments(null)]
  public async Task A_missing_name_is_rejected(string? name)
  {
    var builder = AppModel.CreateBuilder();

    await Assert.That(() => builder.AddReportsServer(name!)).Throws<ArgumentException>();
  }

  private static UpdateCommandStateContext StateContext(string state, IServiceProvider services) =>
    new()
    {
      ResourceSnapshot = new CustomResourceSnapshot
      {
        ResourceType = "Container",
        Properties = ImmutableArray<ResourcePropertySnapshot>.Empty,
        State = new ResourceStateSnapshot(state, null),
      },
      ServiceProvider = services,
    };

  /// <summary>
  /// The repository's deploy/seccomp/chromium.json, which the package must embed unchanged.
  /// </summary>
  private static Task<byte[]> RepositoryProfileAsync() =>
    File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "seccomp", "chromium.json"));
}
