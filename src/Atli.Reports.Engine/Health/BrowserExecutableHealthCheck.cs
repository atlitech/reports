using Atli.Reports.Engine.Chromium.Discovery;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine.Health;

/// <summary>
/// Reports whether the configured browser executable exists.
/// </summary>
internal sealed class BrowserExecutableHealthCheck(IOptions<ReportsEngineOptions> options)
  : IHealthCheck
{
  public Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default
  )
  {
    var browserOptions = options.Value.Browser;
    var path = browserOptions.ExecutablePath;

    if (string.IsNullOrEmpty(path))
    {
      path = BrowserFinder.Find(browserOptions.Kind);
    }

    if (path is not null && File.Exists(path))
    {
      return Task.FromResult(HealthCheckResult.Healthy(path));
    }

    var description = string.IsNullOrEmpty(path)
      ? "No Chromium browser found on this system."
      : $"Browser not found at: {path}";

    return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, description));
  }
}
