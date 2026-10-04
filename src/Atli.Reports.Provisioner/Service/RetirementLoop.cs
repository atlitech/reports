using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// Retires the managed tenants' idle renderers while the service runs, as <c>retire</c> does once:
/// the first time <c>RetireCheckInterval</c> after the service starts, then that long after each
/// run ends. A run that fails is logged, and the next one runs as planned. Nothing runs when
/// <c>RetireAfterIdle</c> is zero.
/// </summary>
/// <param name="service">The prefixes and the retirement settings.</param>
/// <param name="retire">One run, <see cref="RendererProvisioner.RetireIdleAsync"/> in the service.</param>
/// <param name="census">Told of each retired tenant, whose record is gone, so its place is free at once.</param>
/// <param name="time">The clock the interval is measured on.</param>
/// <param name="logger">Where runs are reported.</param>
internal sealed partial class RetirementLoop(
  ProvisioningServiceOptions service,
  Func<CancellationToken, Task<RetireResult>> retire,
  TenantCensus census,
  TimeProvider time,
  ILogger<RetirementLoop> logger
) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (service.RetireAfterIdle <= TimeSpan.Zero)
    {
      LogOff(logger);
      return;
    }

    while (true)
    {
      await Task.Delay(service.RetireCheckInterval, time, stoppingToken);
      await RunAsync(stoppingToken);
    }
  }

  /// <summary>One run; never throws but when <paramref name="stoppingToken"/> stops it.</summary>
  private async Task RunAsync(CancellationToken stoppingToken)
  {
    try
    {
      var result = await retire(stoppingToken);
      foreach (var tenantId in result.Retired)
      {
        census.Deleted(tenantId);
      }

      LogRun(logger, result.Retired.Count, result.Failures.Count);
      foreach (var (tenantId, reason) in result.Failures)
      {
        LogNotRetired(logger, tenantId, reason);
      }
    }
    catch (Exception exception)
      when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
    {
      LogRunFailed(logger, exception, service.RetireCheckInterval);
    }
  }

  [LoggerMessage(
    EventId = 20,
    Level = LogLevel.Information,
    Message = "Retired {Retired} idle renderers; {Failed} could not be retired."
  )]
  private static partial void LogRun(ILogger logger, int retired, int failed);

  [LoggerMessage(
    EventId = 21,
    Level = LogLevel.Warning,
    Message = "Could not retire the renderer of tenant {TenantId}: {Reason}"
  )]
  private static partial void LogNotRetired(ILogger logger, string tenantId, string reason);

  [LoggerMessage(
    EventId = 22,
    Level = LogLevel.Error,
    Message = "The retirement run failed; the next one is in {Interval}."
  )]
  private static partial void LogRunFailed(ILogger logger, Exception exception, TimeSpan interval);

  [LoggerMessage(
    EventId = 23,
    Level = LogLevel.Information,
    Message = "Retirement is off: Provisioner:Service:RetireAfterIdle is 00:00:00."
  )]
  private static partial void LogOff(ILogger logger);
}
