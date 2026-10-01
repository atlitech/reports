using System.Diagnostics.Metrics;
using Atli.Reports.Engine.Diagnostics;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Engine.Tests.Diagnostics;

/// <summary>
/// Checks the instruments the engine's metrics create, on a meter of the test's own, so instruments
/// that other tests create at the same time do not interfere.
/// </summary>
public class EngineMetricsTests
{
  [Test]
  public async Task The_duration_histograms_advise_their_bucket_boundaries()
  {
    using RecordingMeterFactory factory = new();
    using EngineMetrics metrics = new(factory);

    var instruments = PublishedInstruments(factory.Meter);

    await Assert
      .That(BucketsOf(instruments, "atli.reports.conversion.duration"))
      .IsEquivalentTo(EngineMetrics.ConversionDurationBuckets, CollectionOrdering.Matching);
    await Assert
      .That(BucketsOf(instruments, "atli.reports.queue.wait"))
      .IsEquivalentTo(EngineMetrics.QueueWaitBuckets, CollectionOrdering.Matching);
  }

  [Test]
  public async Task The_buckets_are_sized_in_seconds_for_the_default_timeouts()
  {
    var queueTimeout = new ReportsEngineConcurrencyOptions().QueueTimeout.TotalSeconds;
    var commandTimeout = new ReportsEngineBrowserOptions().CommandTimeout.TotalSeconds;

    // Fast conversions and waits do not all share the first bucket, as with OpenTelemetry's
    // default boundaries, which start at 5 and suit milliseconds.
    await Assert.That(EngineMetrics.ConversionDurationBuckets[0]).IsLessThanOrEqualTo(0.01);
    await Assert.That(EngineMetrics.QueueWaitBuckets[0]).IsLessThanOrEqualTo(0.01);

    // A wait that times out lands above a boundary of its own, and a conversion that queued and
    // then waited on a command still lands below the last boundary.
    await Assert.That(EngineMetrics.QueueWaitBuckets).Contains(queueTimeout);
    await Assert
      .That(EngineMetrics.ConversionDurationBuckets[^1])
      .IsGreaterThan(queueTimeout + commandTimeout);
  }

  [Test]
  public async Task The_meter_and_the_activity_source_report_the_engine_version()
  {
    using RecordingMeterFactory factory = new();
    using EngineMetrics metrics = new(factory);
    var expected = typeof(ReportsEngineTelemetry).Assembly.GetName().Version!.ToString(3);

    await Assert.That(factory.Meter.Name).IsEqualTo(ReportsEngineTelemetry.MeterName);
    await Assert.That(factory.Meter.Version).IsEqualTo(expected);
    await Assert
      .That(EngineActivities.Source.Name)
      .IsEqualTo(ReportsEngineTelemetry.ActivitySourceName);
    await Assert.That(EngineActivities.Source.Version).IsEqualTo(expected);
  }

  private static List<Instrument> PublishedInstruments(Meter meter)
  {
    List<Instrument> instruments = [];
    using MeterListener listener = new();
    listener.InstrumentPublished = (instrument, _) =>
    {
      if (instrument.Meter == meter)
      {
        instruments.Add(instrument);
      }
    };

    // Starting publishes every instrument that already exists.
    listener.Start();
    return instruments;
  }

  private static IReadOnlyList<double>? BucketsOf(List<Instrument> instruments, string name) =>
    instruments
      .OfType<Histogram<double>>()
      .Single(instrument => instrument.Name == name)
      .Advice?.HistogramBucketBoundaries;

  /// <summary>
  /// Creates the meter the engine asks for and keeps it, so the test can find its instruments.
  /// </summary>
  private sealed class RecordingMeterFactory : IMeterFactory
  {
    private Meter? _meter;

    public Meter Meter => _meter ?? throw new InvalidOperationException("No meter was created.");

    public Meter Create(MeterOptions options) => _meter = new Meter(options);

    public void Dispose() => _meter?.Dispose();
  }
}
