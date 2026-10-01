using Atli.Reports.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using TUnit.Core.Interfaces;

namespace Atli.Reports.Client.Tests.Support;

/// <summary>
/// The real reports server, in process on a free loopback port, converting in the Chrome (or
/// Chromium) installed on the machine. One server, and so one browser, is shared by the test session.
/// </summary>
/// <remarks>
/// The browser runs without its sandbox: Ubuntu 24.04 runners block the user namespaces the sandbox
/// needs, and the HTML here is trusted. Timeouts are generous because a two-core CI runner can be
/// slow to start a browser.
/// </remarks>
public sealed class RunningReportsServer : IAsyncInitializer, IAsyncDisposable
{
  private WebApplication? _app;

  /// <summary>
  /// The server's base address.
  /// </summary>
  public Uri Endpoint => new(_app!.Urls.First());

  public async Task InitializeAsync()
  {
    _app = ReportsServerApplication.Create(
      [
        "--urls=http://127.0.0.1:0",
        "--ReportsEngine:Browser:WarmUpOnStartup=false",
        "--ReportsEngine:Browser:NoSandbox=true",
        "--ReportsEngine:Browser:DisableDevShmUsage=true",
        "--ReportsEngine:Browser:StartupTimeout=00:01:00",
        "--ReportsEngine:Browser:CommandTimeout=00:00:30",
      ],
      builder =>
      {
        builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
        builder.Logging.ClearProviders();
      }
    );
    await _app.StartAsync();
  }

  public async ValueTask DisposeAsync()
  {
    if (_app is not null)
    {
      await _app.StopAsync(CancellationToken.None);
      await _app.DisposeAsync();
    }
  }
}
