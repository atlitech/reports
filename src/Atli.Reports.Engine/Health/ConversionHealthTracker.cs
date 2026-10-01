namespace Atli.Reports.Engine.Health;

/// <summary>
/// Thread-safe tracker that records recent conversion outcomes for health reporting.
/// Uses time-windowed success rate for high-traffic scenarios and consecutive failure
/// streak detection for low-traffic scenarios. Stale data (no activity for 120s)
/// automatically expires to prevent Kubernetes readiness probe deadlocks.
/// </summary>
internal sealed class ConversionHealthTracker(TimeProvider timeProvider)
{
  private const int BufferSize = 20;
  private const int MinSamples = 3;
  private const double UnhealthyThreshold = 0.5;
  private const int ConsecutiveFailureThreshold = 3;
  private static readonly long ExpiryWindowTicks = TimeSpan.FromSeconds(120).Ticks;

  private readonly TimestampedResult[] _results = new TimestampedResult[BufferSize];
  private readonly Lock _lock = new();
  private int _index;
  private int _count;
  private int _consecutiveFailures;
  private long _lastRecordTicks;
  private string? _lastFailureReason;

  public void RecordSuccess()
  {
    lock (_lock)
    {
      var now = timeProvider.GetTimestamp();
      ResetIfStale(now);

      _results[_index] = new TimestampedResult(now, true);
      _consecutiveFailures = 0;
      _lastRecordTicks = now;
      Advance();
    }
  }

  public void RecordFailure(string? reason = null)
  {
    lock (_lock)
    {
      var now = timeProvider.GetTimestamp();
      ResetIfStale(now);

      _results[_index] = new TimestampedResult(now, false);
      _consecutiveFailures++;
      _lastFailureReason = reason;
      _lastRecordTicks = now;
      Advance();
    }
  }

  public ConversionHealthStatus GetHealthStatus()
  {
    lock (_lock)
    {
      if (_count == 0)
      {
        return new ConversionHealthStatus(0, 0, 0, 1.0, null, 0);
      }

      var now = timeProvider.GetTimestamp();
      var elapsed = timeProvider.GetElapsedTime(_lastRecordTicks, now);

      if (elapsed.Ticks > ExpiryWindowTicks)
      {
        return new ConversionHealthStatus(0, 0, 0, 1.0, null, _consecutiveFailures);
      }

      var total = Math.Min(_count, BufferSize);
      var successes = 0;
      var counted = 0;

      for (var i = 0; i < total; i++)
      {
        ref var entry = ref _results[i];
        if (
          timeProvider.GetElapsedTime(entry.TimestampTicks, now).Ticks <= ExpiryWindowTicks
          && entry.TimestampTicks != 0
        )
        {
          counted++;
          if (entry.Success)
          {
            successes++;
          }
        }
      }

      var failures = counted - successes;
      var successRate = counted > 0 ? (double)successes / counted : 1.0;

      return new ConversionHealthStatus(
        counted,
        successes,
        failures,
        successRate,
        _lastFailureReason,
        _consecutiveFailures
      );
    }
  }

  public bool IsHealthy
  {
    get
    {
      var status = GetHealthStatus();

      // No data yet OR stale data (no activity in expiry window) → healthy (deadlock recovery)
      if (status.Total == 0)
      {
        return true;
      }

      // Enough non-expired samples → use success rate
      if (status.Total >= MinSamples)
      {
        return status.SuccessRate >= UnhealthyThreshold;
      }

      // Low-traffic: consecutive failures hit threshold → unhealthy
      if (status.ConsecutiveFailures >= ConsecutiveFailureThreshold)
      {
        return false;
      }

      // Not enough data to determine → healthy
      return true;
    }
  }

  private void ResetIfStale(long now)
  {
    if (
      _lastRecordTicks != 0
      && timeProvider.GetElapsedTime(_lastRecordTicks, now).Ticks > ExpiryWindowTicks
    )
    {
      _consecutiveFailures = 0;
    }
  }

  private void Advance()
  {
    _index = (_index + 1) % BufferSize;
    if (_count < BufferSize)
    {
      _count++;
    }
  }

  private readonly struct TimestampedResult(long timestampTicks, bool success)
  {
    public long TimestampTicks { get; } = timestampTicks;
    public bool Success { get; } = success;
  }
}

internal sealed record ConversionHealthStatus(
  int Total,
  int Successes,
  int Failures,
  double SuccessRate,
  string? LastFailureReason,
  int ConsecutiveFailures
);
