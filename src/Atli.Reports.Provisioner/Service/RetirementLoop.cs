using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// Retires the managed tenants' idle renderers while the service runs, as <c>retire</c> does once,
/// and deletes their leftover sandboxes: the first time <see cref="FirstRunAfter"/> after the
/// service starts, then <c>RetireCheckInterval</c> after each run ends. A run that fails is logged,
/// and the next one runs as planned. With <c>RetireAfterIdle</c> at zero, runs only delete
/// leftovers.
/// </summary>
/// <param name="service">The prefixes and the retirement settings.</param>
/// <param name="retire">
/// One run, <see cref="RendererProvisioner.RetireIdleAsync(ProvisioningServiceOptions, bool, CancellationToken)"/>
/// with leftovers, in the service.
/// </param>
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
  /// <summary>
  /// How long after the service starts the first run begins: soon, so that a service restarted
  /// more often than <c>RetireCheckInterval</c> still runs, but not while it is starting.
  /// </summary>
  public static readonly TimeSpan FirstRunAfter = TimeSpan.FromMinutes(1);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (service.RetireAfterIdle <= TimeSpan.Zero)
    {
      LogOff(logger);
    }

    await Task.Delay(FirstRunAfter, time, stoppingToken);
    while (true)
    {
      await RunAsync(stoppingToken);
      await Task.Delay(service.RetireCheckInterval, time, stoppingToken);
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

      LogRun(logger, result.Retired.Count, result.Pruned.Count, result.Failures.Count);
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
    Message = "Retired {Retired} idle renderers and deleted {Pruned} leftover sandboxes; {Failed} tenants failed."
  )]
  private static partial void LogRun(ILogger logger, int retired, int pruned, int failed);

  [LoggerMessage(
    EventId = 21,
    Level = LogLevel.Warning,
    Message = "Could not retire tenant {TenantId}, or delete its leftovers: {Reason}"
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
    Message = "Retirement is off: Provisioner:Service:RetireAfterIdle is 00:00:00. Leftover sandboxes are still deleted."
  )]
  private static partial void LogOff(ILogger logger);
}
