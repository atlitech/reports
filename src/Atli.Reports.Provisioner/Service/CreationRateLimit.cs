namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// <c>Provisioner:Service:MaxCreatesPerMinute</c> as a sliding window: a create may start while
/// fewer than the most started in the minute before it, whether they succeeded or not. Not
/// thread-safe; <see cref="ManagedRenderers"/> calls it under its lock.
/// </summary>
internal sealed class CreationRateLimit(int perMinute, TimeProvider time)
{
  public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

  private readonly Queue<long> _started = new();

  /// <summary>
  /// Counts a create that starts now and returns <see langword="true"/>, or returns
  /// <see langword="false"/> with how long until one may.
  /// </summary>
  public bool TryAcquire(out TimeSpan retryAfter)
  {
    var now = time.GetTimestamp();
    while (_started.Count > 0 && time.GetElapsedTime(_started.Peek(), now) >= Window)
    {
      _started.Dequeue();
    }

    if (_started.Count < perMinute)
    {
      _started.Enqueue(now);
      retryAfter = TimeSpan.Zero;
      return true;
    }

    retryAfter = Window - time.GetElapsedTime(_started.Peek(), now);
    return false;
  }
}
