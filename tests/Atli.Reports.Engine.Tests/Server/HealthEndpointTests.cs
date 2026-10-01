using System.Net;
using System.Text.Json;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Engine.Tests.Server;

/// <summary>
/// The server's health endpoints when the browser cannot start, starts again, or has not started
/// yet. Fake browsers stand in for one that cannot start.
/// </summary>
public class HealthEndpointTests
{
  private const string MissingLibrary =
    "echo 'chrome-headless-shell: error while loading shared libraries: libnss3.so: cannot open shared object file: No such file or directory' >&2; exit 127";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task A_browser_that_fails_to_start_makes_the_server_unready_with_the_reason()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create(MissingLibrary);
    await using var server = await RunningServer.StartAsync(
      converter: null,
      "--ReportsEngine:Browser:WarmUpOnStartup=true",
      $"--ReportsEngine:Browser:ExecutablePath={fake.Path}"
    );

    var ready = await GetHealthAsync(server, "/health/ready");
    var live = await GetHealthAsync(server, "/health/live");

    await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    await Assert.That(ready.Status).IsEqualTo("Unhealthy");
    await Assert.That(ready.Checks["browser"].Status).IsEqualTo("Unhealthy");
    await Assert
      .That(ready.Checks["browser"].Description)
      .StartsWith("The browser failed to start (")
      .And.Contains("retrying in the background")
      .And.Contains("exited with code 127")
      .And.Contains("libnss3.so: cannot open shared object file");
    await Assert.That(ready.Checks["conversion_health"].Status).IsEqualTo("Healthy");
    await Assert
      .That(live.StatusCode)
      .IsEqualTo(HttpStatusCode.OK)
      .Because("restarting the server would not repair the browser");
  }

  [Test]
  [NotInParallel("chrome")]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Once_the_browser_can_start_the_server_becomes_ready_without_a_conversion(
    bool warmUp
  )
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create(MissingLibrary);
    await using var server = await RunningServer.StartAsync(
      converter: null,
      $"--ReportsEngine:Browser:WarmUpOnStartup={warmUp}",
      $"--ReportsEngine:Browser:ExecutablePath={fake.Path}",
      "--ReportsEngine:Browser:DisableDevShmUsage=true",
      "--ReportsEngine:Browser:StartupTimeout=00:01:00",
      $"--ReportsEngine:Browser:CommandTimeout={TestEngine.GenerousTimeout}"
    );
    if (!warmUp)
    {
      // Without warm-up, the first conversion launches the browser.
      using var converted = await server.PostAsync("""{"html":"<p>x</p>"}""");
      await Assert.That(converted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    var failing = await GetHealthAsync(server, "/health/ready");
    await Assert.That(failing.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    await Assert.That(failing.Checks["browser"].Status).IsEqualTo("Unhealthy");

    // The missing library is installed: the next launch starts the real browser.
    fake.Replace(FakeBrowser.RealChrome());

    HealthResponse? recovered = null;
    await Assert
      .That(
        await TestEngine.EventuallyAsync(
          async () =>
          {
            recovered = await GetHealthAsync(server, "/health/ready");
            return recovered.StatusCode == HttpStatusCode.OK;
          },
          TimeSpan.FromSeconds(90)
        )
      )
      .IsTrue()
      .Because("the engine retries the launch in the background");
    await Assert.That(recovered!.Checks["browser"].Status).IsEqualTo("Healthy");
    await Assert.That(recovered.Checks["browser"].Description).Contains("is running");
    await Assert.That(server.Services.GetRequiredService<BrowserManager>().Current).IsNotNull();
    await Assert.That(fake.CountLaunches()).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  public async Task Without_warm_up_the_server_is_ready_before_the_browser_first_starts()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create(MissingLibrary);
    await using var server = await RunningServer.StartAsync(
      converter: null,
      "--ReportsEngine:Browser:WarmUpOnStartup=false",
      $"--ReportsEngine:Browser:ExecutablePath={fake.Path}"
    );

    var ready = await GetHealthAsync(server, "/health/ready");

    await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(ready.Checks["browser"].Status).IsEqualTo("Healthy");
    await Assert.That(ready.Checks["browser"].Description).Contains(fake.Path);
    await Assert.That(fake.CountLaunches()).IsEqualTo(0).Because("the first conversion starts it");
  }

  [Test]
  public async Task A_missing_browser_executable_makes_the_server_unready()
  {
    var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "chrome");
    await using var server = await RunningServer.StartAsync(
      converter: null,
      $"--ReportsEngine:Browser:ExecutablePath={missing}"
    );

    var ready = await GetHealthAsync(server, "/health/ready");

    await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    await Assert
      .That(ready.MediaType)
      .IsEqualTo("application/json")
      .Because("the health report is the body, not problem details");
    await Assert.That(ready.Checks["browser"].Status).IsEqualTo("Unhealthy");
    await Assert.That(ready.Checks["browser"].Description).Contains("does not exist");
  }

  [Test]
  public async Task Liveness_reports_healthy_without_running_a_check()
  {
    await using var server = await RunningServer.StartAsync(converter: null);

    var live = await GetHealthAsync(server, "/health/live");

    await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(live.MediaType).IsEqualTo("application/json");
    await Assert.That(live.Status).IsEqualTo("Healthy");
    await Assert.That(live.Checks).IsEmpty();
  }

  private static async Task<HealthResponse> GetHealthAsync(RunningServer server, string path)
  {
    using var response = await server.Client.GetAsync(path, TestToken);
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestToken));
    var checks = body
      .RootElement.GetProperty("checks")
      .EnumerateObject()
      .ToDictionary(
        check => check.Name,
        check => new HealthCheckEntry(
          check.Value.GetProperty("status").GetString()!,
          check.Value.TryGetProperty("description", out var description)
            ? description.GetString()
            : null
        )
      );
    return new HealthResponse(
      response.StatusCode,
      response.Content.Headers.ContentType?.MediaType,
      body.RootElement.GetProperty("status").GetString()!,
      checks
    );
  }

  private sealed record HealthResponse(
    HttpStatusCode StatusCode,
    string? MediaType,
    string Status,
    IReadOnlyDictionary<string, HealthCheckEntry> Checks
  );

  private sealed record HealthCheckEntry(string Status, string? Description);
}
