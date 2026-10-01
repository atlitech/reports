using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Atli.Reports.Engine.Diagnostics;

/// <summary>
/// The engine's metrics, published on the <c>Atli.Reports.Engine</c> meter.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Instrument</term><description>Meaning</description></listheader>
/// <item><term><c>atli.reports.conversion.duration</c> (histogram, s)</term><description>Duration of finished conversions, including the wait in the queue; tagged <c>outcome</c> = <c>success</c> or the <see cref="ConversionErrorKind"/>.</description></item>
/// <item><term><c>atli.reports.conversion.active</c> (up-down counter)</term><description>Conversions holding a slot.</description></item>
/// <item><term><c>atli.reports.queue.length</c> (up-down counter)</term><description>Conversions waiting for a slot.</description></item>
/// <item><term><c>atli.reports.queue.wait</c> (histogram, s)</term><description>Time conversions waited for a slot.</description></item>
/// <item><term><c>atli.reports.browser.launches</c> (counter)</term><description>Browser processes started.</description></item>
/// <item><term><c>atli.reports.browser.crashes</c> (counter)</term><description>Browser processes that exited or disconnected unexpectedly.</description></item>
/// <item><term><c>atli.reports.browser.recycles</c> (counter)</term><description>Browser processes replaced because of their age or conversion count.</description></item>
/// </list>
/// </remarks>
internal sealed class EngineMetrics : IDisposable
{
  /// <summary>
  /// The name of the engine's meter.
  /// </summary>
  public const string MeterName = "Atli.Reports.Engine";

  private readonly Meter _meter;
  private readonly bool _ownsMeter;
  private readonly Histogram<double> _conversionDuration;
  private readonly UpDownCounter<long> _activeConversions;
  private readonly UpDownCounter<long> _queueLength;
  private readonly Histogram<double> _queueWait;
  private readonly Counter<long> _browserLaunches;
  private readonly Counter<long> _browserCrashes;
  private readonly Counter<long> _browserRecycles;

  public EngineMetrics(IMeterFactory? meterFactory = null)
  {
    _ownsMeter = meterFactory is null;
    _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
    _conversionDuration = _meter.CreateHistogram<double>(
      "atli.reports.conversion.duration",
      unit: "s",
      description: "Duration of finished conversions, including the wait in the queue."
    );
    _activeConversions = _meter.CreateUpDownCounter<long>(
      "atli.reports.conversion.active",
      unit: "{conversion}",
      description: "Conversions holding a slot."
    );
    _queueLength = _meter.CreateUpDownCounter<long>(
      "atli.reports.queue.length",
      unit: "{conversion}",
      description: "Conversions waiting for a slot."
    );
    _queueWait = _meter.CreateHistogram<double>(
      "atli.reports.queue.wait",
      unit: "s",
      description: "Time conversions waited for a slot."
    );
    _browserLaunches = _meter.CreateCounter<long>(
      "atli.reports.browser.launches",
      unit: "{process}",
      description: "Browser processes started."
    );
    _browserCrashes = _meter.CreateCounter<long>(
      "atli.reports.browser.crashes",
      unit: "{process}",
      description: "Browser processes that exited or disconnected unexpectedly."
    );
    _browserRecycles = _meter.CreateCounter<long>(
      "atli.reports.browser.recycles",
      unit: "{process}",
      description: "Browser processes replaced because of their age or conversion count."
    );
  }

  public void ConversionFinished(TimeSpan duration, ConversionErrorKind? errorKind) =>
    _conversionDuration.Record(
      duration.TotalSeconds,
      new KeyValuePair<string, object?>("outcome", errorKind?.ToString() ?? "success")
    );

  public void SlotTaken() => _activeConversions.Add(1);

  public void SlotReleased() => _activeConversions.Add(-1);

  public void Enqueued() => _queueLength.Add(1);

  public void Dequeued(long startedTimestamp)
  {
    _queueLength.Add(-1);
    _queueWait.Record(Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds);
  }

  public void BrowserLaunched() => _browserLaunches.Add(1);

  public void BrowserCrashed() => _browserCrashes.Add(1);

  public void BrowserRecycled() => _browserRecycles.Add(1);

  public void Dispose()
  {
    if (_ownsMeter)
    {
      _meter.Dispose();
    }
  }
}
