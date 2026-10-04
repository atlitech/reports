namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// A <c>MaxCreatesPerMinute</c>, a prefix's or the service's, as a sliding window: a create may
/// start while fewer than the most started in the minute before it, whether they succeeded or not.
/// Not thread-safe; <see cref="ManagedRenderers"/> calls it under its lock.
/// </summary>
/// <param name="perMinute">The most creates that may start in any minute.</param>
/// <param name="time">The clock the window is measured on.</param>
internal sealed class CreationRateLimit(int perMinute, TimeProvider time)
{
  public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

  private readonly Queue<long> _started = new();

  /// <summary>The most creates that may start in any minute.</summary>
  public int PerMinute => perMinute;

  /// <summary>
  /// Whether a create may start at <paramref name="now"/>, a timestamp of the clock; when not, how
  /// long until one may. Counts nothing: <see cref="Take"/> does, once the create starts.
  /// </summary>
  public bool HasRoom(long now, out TimeSpan retryAfter)
  {
    while (_started.Count > 0 && time.GetElapsedTime(_started.Peek(), now) >= Window)
    {
      _started.Dequeue();
    }

    if (_started.Count < perMinute)
    {
      retryAfter = TimeSpan.Zero;
      return true;
    }

    retryAfter = Window - time.GetElapsedTime(_started.Peek(), now);
    return false;
  }

  /// <summary>Counts a create that starts at <paramref name="now"/>, after <see cref="HasRoom"/> found room.</summary>
  public void Take(long now) => _started.Enqueue(now);
}
