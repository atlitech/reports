using Atli.Reports.Engine.Health;

namespace Atli.Reports.Engine.Tests.Health;

public class ConversionHealthTrackerTests
{
  [Test]
  public async Task No_conversions_is_healthy()
  {
    ConversionHealthTracker tracker = new(new ManualTimeProvider());

    await Assert.That(tracker.GetHealthStatus().IsHealthy).IsTrue();
    await Assert.That(tracker.GetHealthStatus().Total).IsEqualTo(0);
  }

  [Test]
  public async Task A_low_success_rate_is_unhealthy()
  {
    ConversionHealthTracker tracker = new(new ManualTimeProvider());

    tracker.RecordSuccess();
    tracker.RecordFailure();
    tracker.RecordFailure();

    var status = tracker.GetHealthStatus();
    await Assert.That(tracker.GetHealthStatus().IsHealthy).IsFalse();
    await Assert.That(status.Successes).IsEqualTo(1);
    await Assert.That(status.Failures).IsEqualTo(2);
  }

  [Test]
  public async Task Failures_older_than_two_minutes_expire()
  {
    ManualTimeProvider time = new();
    ConversionHealthTracker tracker = new(time);
    tracker.RecordFailure();
    tracker.RecordFailure();
    tracker.RecordFailure();

    time.Advance(TimeSpan.FromSeconds(121));

    await Assert.That(tracker.GetHealthStatus().IsHealthy).IsTrue();
    await Assert.That(tracker.GetHealthStatus().Total).IsEqualTo(0);
  }

  [Test]
  public async Task A_success_resets_the_consecutive_failure_count()
  {
    ConversionHealthTracker tracker = new(new ManualTimeProvider());
    tracker.RecordFailure();
    tracker.RecordFailure();

    tracker.RecordSuccess();

    await Assert.That(tracker.GetHealthStatus().ConsecutiveFailures).IsEqualTo(0);
  }

  [Test]
  public async Task A_snapshot_keeps_its_decision_when_later_conversions_recover()
  {
    ConversionHealthTracker tracker = new(new ManualTimeProvider());
    tracker.RecordFailure();
    tracker.RecordFailure();
    tracker.RecordFailure();
    var failed = tracker.GetHealthStatus();

    tracker.RecordSuccess();
    tracker.RecordSuccess();
    tracker.RecordSuccess();

    await Assert.That(failed.IsHealthy).IsFalse();
    await Assert.That(failed.Failures).IsEqualTo(3);
    await Assert.That(tracker.GetHealthStatus().IsHealthy).IsTrue();
  }

  [Test]
  public async Task An_unbroken_failure_streak_stays_unhealthy_as_older_samples_expire()
  {
    ManualTimeProvider time = new();
    ConversionHealthTracker tracker = new(time);
    tracker.RecordFailure();
    time.Advance(TimeSpan.FromSeconds(60));
    tracker.RecordFailure();
    time.Advance(TimeSpan.FromSeconds(61));
    tracker.RecordFailure();

    var status = tracker.GetHealthStatus();
    await Assert.That(status.Total).IsEqualTo(2);
    await Assert.That(status.ConsecutiveFailures).IsEqualTo(3);
    await Assert.That(status.IsHealthy).IsFalse();
  }

  /// <summary>
  /// A time provider whose clock only moves when the test advances it.
  /// </summary>
  private sealed class ManualTimeProvider : TimeProvider
  {
    private long _timestamp = 1;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by) => _timestamp += by.Ticks;
  }
}
