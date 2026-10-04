using Microsoft.Extensions.Time.Testing;

namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>
/// The provisioner's fake clock, which keeps track of the timers scheduled on it: the readiness
/// polls and the drains.
/// </summary>
/// <remarks>
/// The provisioner schedules its timers on its own threads, and advancing the clock before a timer
/// is scheduled moves the timer instead of firing it. So a test waits for the timer it means to
/// fire, then advances the clock: the delay ends exactly when the clock reaches it, with no time
/// passing on the wall clock. It starts at 2026-10-03 12:00 UTC.
/// </remarks>
internal sealed class TestClock : FakeTimeProvider
{
  /// <summary>
  /// The smallest step of the clock. A test advances the clock to one tick before a timer is due, to
  /// show it has not fired early, then by one more tick to fire it.
  /// </summary>
  public static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

  /// <summary>
  /// How long to wait, on the wall clock, for a timer the provisioner does not schedule before
  /// failing. Only a failing test waits this long.
  /// </summary>
  private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

  private readonly Lock _lock = new();
  private readonly HashSet<ScheduledTimer> _scheduled = [];
  private TaskCompletionSource _scheduling = NewSignal();

  public TestClock()
    : base(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)) { }

  public override ITimer CreateTimer(
    TimerCallback callback,
    object? state,
    TimeSpan dueTime,
    TimeSpan period
  )
  {
    ScheduledTimer timer = new(this, callback, state);
    timer.Change(dueTime, period);
    return timer;
  }

  /// <summary>Waits until a timer is scheduled to fire exactly <paramref name="dueIn"/> from now.</summary>
  public async Task WaitForTimerAsync(TimeSpan dueIn)
  {
    using CancellationTokenSource giveUp = new(GiveUpAfter);
    while (true)
    {
      var now = GetUtcNow();
      Task scheduling;
      lock (_lock)
      {
        if (_scheduled.Any(timer => timer.DueAt - now == dueIn))
        {
          return;
        }

        scheduling = _scheduling.Task;
      }

      try
      {
        await scheduling.WaitAsync(giveUp.Token);
      }
      catch (OperationCanceledException)
      {
        throw new TimeoutException(
          $"No timer was scheduled to fire in {dueIn}. Scheduled to fire in: {Describe(now)}."
        );
      }
    }
  }

  private string Describe(DateTimeOffset now)
  {
    lock (_lock)
    {
      return _scheduled.Count == 0
        ? "none"
        : string.Join(", ", _scheduled.Select(timer => timer.DueAt - now).Order());
    }
  }

  private ITimer CreateFakeTimer(TimerCallback callback, object? state) =>
    base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

  /// <summary>Takes <paramref name="timer"/> off the books, and returns its new version.</summary>
  private int Unschedule(ScheduledTimer timer)
  {
    lock (_lock)
    {
      _scheduled.Remove(timer);
      return ++timer.Version;
    }
  }

  /// <summary>
  /// Puts <paramref name="timer"/> on the books, unless it fired, changed, or was disposed since
  /// <paramref name="version"/>.
  /// </summary>
  private void Schedule(ScheduledTimer timer, int version, DateTimeOffset dueAt)
  {
    lock (_lock)
    {
      if (timer.Version != version)
      {
        return;
      }

      timer.DueAt = dueAt;
      _scheduled.Add(timer);
      _scheduling.SetResult();
      _scheduling = NewSignal();
    }
  }

  private static TaskCompletionSource NewSignal() =>
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>
  /// A timer of the fake clock that is on the clock's books while it is scheduled. The provisioner's
  /// timers fire once, so firing takes it off.
  /// </summary>
  private sealed class ScheduledTimer : ITimer
  {
    private readonly TestClock _clock;
    private readonly TimerCallback _callback;
    private readonly ITimer _timer;

    public ScheduledTimer(TestClock clock, TimerCallback callback, object? state)
    {
      _clock = clock;
      _callback = callback;
      _timer = clock.CreateFakeTimer(Fire, state);
    }

    /// <summary>Changes on every change, firing, and disposal, under the clock's lock.</summary>
    public int Version { get; set; }

    public DateTimeOffset DueAt { get; set; }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
      var version = _clock.Unschedule(this);
      var dueAt = _clock.GetUtcNow() + dueTime;
      // Scheduled first and booked after, so a test that finds the timer on the books can fire it.
      var changed = _timer.Change(dueTime, period);
      if (dueTime != Timeout.InfiniteTimeSpan)
      {
        _clock.Schedule(this, version, dueAt);
      }

      return changed;
    }

    public void Dispose()
    {
      _clock.Unschedule(this);
      _timer.Dispose();
    }

    public ValueTask DisposeAsync()
    {
      Dispose();
      return ValueTask.CompletedTask;
    }

    private void Fire(object? state)
    {
      _clock.Unschedule(this);
      _callback(state);
    }
  }
}
