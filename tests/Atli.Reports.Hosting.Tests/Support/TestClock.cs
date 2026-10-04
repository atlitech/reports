using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Atli.Reports.Hosting.Tests.Support;

/// <summary>
/// A fake clock that reports each timer scheduled on it, so a test can wait for a retry delay to
/// start before it advances the clock past it.
/// </summary>
/// <remarks>
/// Advancing the clock before a delay is scheduled would not fire the delay; it would start from
/// the new time. So a test first takes the delay from <see cref="NextTimerAsync"/>, then advances.
/// </remarks>
internal sealed class TestClock : FakeTimeProvider
{
  /// <summary>The clock's smallest step.</summary>
  public static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

  private readonly Channel<TimeSpan> _scheduled = Channel.CreateUnbounded<TimeSpan>();

  public override ITimer CreateTimer(
    TimerCallback callback,
    object? state,
    TimeSpan dueTime,
    TimeSpan period
  )
  {
    var timer = base.CreateTimer(callback, state, dueTime, period);
    if (dueTime != Timeout.InfiniteTimeSpan)
    {
      _scheduled.Writer.TryWrite(dueTime);
    }

    return timer;
  }

  /// <summary>Waits for the next timer to be scheduled and returns how long it is due in.</summary>
  public async Task<TimeSpan> NextTimerAsync(CancellationToken cancellationToken)
  {
    using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    giveUp.CancelAfter(TimeSpan.FromSeconds(30));
    try
    {
      return await _scheduled.Reader.ReadAsync(giveUp.Token);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      throw new TimeoutException("No timer was scheduled.");
    }
  }

  /// <summary>Whether a timer was scheduled that no test has taken yet.</summary>
  public bool HasPendingTimer => _scheduled.Reader.Count > 0;
}
