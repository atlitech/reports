using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Engine.Health;

/// <summary>
/// Reports the outcome of recent conversions recorded by <see cref="ConversionHealthTracker"/>.
/// </summary>
internal sealed class ConversionHealthCheck(ConversionHealthTracker tracker) : IHealthCheck
{
  public Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default
  )
  {
    var status = tracker.GetHealthStatus();

    if (status.Total == 0)
    {
      return Task.FromResult(HealthCheckResult.Healthy("No conversions yet."));
    }

    var description = $"{status.Successes}/{status.Total} succeeded ({status.SuccessRate:P0})";

    if (status.ConsecutiveFailures > 0)
    {
      description += $" — {status.ConsecutiveFailures} consecutive failure(s)";
    }

    if (status.IsHealthy)
    {
      return Task.FromResult(HealthCheckResult.Healthy(description));
    }

    return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, description));
  }
}
