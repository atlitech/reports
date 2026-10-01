using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine.Hosting;

/// <summary>
/// Ties the engine's browser to the host's lifetime: optionally starts it with the host, and drains
/// and closes it when the host stops.
/// </summary>
internal sealed class ReportsEngineHostedService(
  BrowserManager browsers,
  IOptions<ReportsEngineOptions> options,
  ILogger<ReportsEngineHostedService> logger
) : IHostedService
{
  public async Task StartAsync(CancellationToken cancellationToken)
  {
    if (!options.Value.Browser.WarmUpOnStartup)
    {
      return;
    }

    try
    {
      await browsers.WarmUpAsync(cancellationToken);
    }
    catch (BrowserUnavailableException exception)
    {
      // A browser that cannot start must not stop the host. The browser health check reports the
      // failure, and the engine keeps retrying the launch in the background.
      LogMessages.WarmUpFailed(logger, exception);
    }
  }

  public Task StopAsync(CancellationToken cancellationToken) =>
    browsers.ShutdownAsync(options.Value.Browser.ShutdownTimeout, cancellationToken);
}
