using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Core.Interfaces;

namespace Atli.Reports.AppHost.Tests.Support;

/// <summary>
/// The examples' AppHost (examples/Atli.Reports.AppHost), started once for the test session with
/// Aspire.Hosting.Testing: the reports server container, built from its Dockerfile, the example apps,
/// and the Tailwind build.
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator (DCP) comes from the Aspire CLI bundle, as the AppHost sets
/// <c>AspireUseCliBundle</c>. Building the AppHost resolves the bundle, from the installed Aspire CLI
/// or, without one, from the CLI release paired with the AppHost's SDK, which the build fetches with
/// <c>dnx</c>. It writes the bundle's paths into the AppHost assembly, where Aspire.Hosting.Testing
/// reads them. The dashboard does not run in tests.
/// </para>
/// <para>
/// Each resource's console output goes to <see cref="LogDirectory"/>, one file per resource.
/// </para>
/// </remarks>
public sealed class ReportsAppHost : IAsyncInitializer, IAsyncDisposable
{
  public const string ReportsServer = "reports-server";
  public const string RemoteExample = "remote-report-server";
  public const string SimpleExample = "simple-report-server";
  public const string TailwindExample = "tailwind-report-server";

  /// <summary>
  /// The longest the AppHost may take to start. Starting includes building the reports server's
  /// image, three to four minutes on a GitHub runner without a build cache. The resources then turn
  /// healthy in the background; tests wait for the ones they use.
  /// </summary>
  private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(10);

  /// <summary>
  /// The longest stopping the resources and removing the server's container may take.
  /// </summary>
  private static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(2);

  private readonly FileLoggerProvider _logs;
  private DistributedApplication? _app;

  public ReportsAppHost()
  {
    if (Directory.Exists(LogDirectory))
    {
      Directory.Delete(LogDirectory, recursive: true);
    }

    _logs = new(LogDirectory);
  }

  /// <summary>
  /// Where the resources' logs go: <c>TestResults/resource-logs</c> in the test project's output.
  /// </summary>
  public static string LogDirectory { get; } =
    Path.Combine(AppContext.BaseDirectory, "TestResults", "resource-logs");

  private DistributedApplication App =>
    _app ?? throw new InvalidOperationException("The AppHost has not started.");

  public async Task InitializeAsync()
  {
    // IAsyncInitializer passes no token, and the AppHost is shared by every test, so its start is
    // bounded on its own rather than by the first test's token.
    using CancellationTokenSource timeout = new(StartTimeout);
    var builder =
      await DistributedApplicationTestingBuilder.CreateAsync<Projects.Atli_Reports_AppHost>(
        timeout.Token
      );

    builder.Services.AddLogging(logging => logging.AddProvider(_logs));

    // The in-process examples start the browser installed on this machine. Ubuntu 24.04, as on
    // GitHub's ubuntu-latest runners, blocks the user namespaces Chrome's sandbox needs; the tests'
    // pages are trusted, so the examples run the browser without its sandbox, as the engine's own
    // tests do.
    foreach (var example in new[] { SimpleExample, TailwindExample })
    {
      builder
        .CreateResourceBuilder<ProjectResource>(example)
        .WithEnvironment("ReportsEngine__Browser__NoSandbox", "true");
    }

    _app = await builder.BuildAsync(timeout.Token);
    await _app.StartAsync(timeout.Token);
  }

  /// <summary>
  /// Waits until the resource's health checks pass, and fails at once if the resource cannot start.
  /// </summary>
  public async Task WaitForHealthyAsync(string resourceName, CancellationToken cancellationToken)
  {
    try
    {
      await App.ResourceNotifications.WaitForResourceHealthyAsync(
        resourceName,
        WaitBehavior.StopOnResourceUnavailable,
        cancellationToken
      );
    }
    catch (Exception exception)
      when (exception is OperationCanceledException or DistributedApplicationException)
    {
      throw new InvalidOperationException(
        $"'{resourceName}' did not become healthy. Resources: {DescribeResources()}. "
          + $"Their logs are in {LogDirectory}.",
        exception
      );
    }
  }

  /// <summary>
  /// A client for the resource's <c>http</c> endpoint.
  /// </summary>
  public HttpClient CreateHttpClient(string resourceName) =>
    App.CreateHttpClient(resourceName, "http");

  /// <summary>
  /// The ID of the process the orchestrator started for a project resource.
  /// </summary>
  public int GetProcessId(string resourceName)
  {
    if (
      App.ResourceNotifications.TryGetCurrentState(resourceName, out var resourceEvent)
      && resourceEvent.Snapshot.Properties.FirstOrDefault(property =>
        property.Name == "executable.pid"
      )
        is { Value: { } processId }
    )
    {
      return Convert.ToInt32(processId, CultureInfo.InvariantCulture);
    }

    throw new InvalidOperationException($"'{resourceName}' has no process ID.");
  }

  public async ValueTask DisposeAsync()
  {
    if (_app is not null)
    {
      using CancellationTokenSource timeout = new(StopTimeout);
      try
      {
        await _app.StopAsync(timeout.Token);
      }
      catch (OperationCanceledException)
      {
        // Disposing the app still tears the resources down.
      }

      await _app.DisposeAsync();
    }

    _logs.Dispose();
  }

  private string DescribeResources()
  {
    var model = App.Services.GetRequiredService<DistributedApplicationModel>();
    return string.Join(
      "; ",
      model.Resources.Select(resource =>
        App.ResourceNotifications.TryGetCurrentState(resource.Name, out var resourceEvent)
          ? $"{resource.Name} {resourceEvent.Snapshot.State?.Text ?? "(no state)"}"
            + $" {resourceEvent.Snapshot.HealthStatus?.ToString() ?? "(no health)"}"
          : $"{resource.Name} (not started)"
      )
    );
  }
}
