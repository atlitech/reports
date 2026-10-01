using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// Opens isolated pages for conversions.
/// </summary>
internal interface IBrowserProvider
{
  /// <summary>
  /// Opens a page dedicated to one conversion, starting a browser first if none is running.
  /// </summary>
  /// <exception cref="BrowserUnavailableException">No browser can serve the conversion.</exception>
  ValueTask<IConversionPage> OpenPageAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns the engine's long-lived browser: starts it on first use (or at host start-up), replaces it
/// when it crashes, recycles it after <see cref="ReportsEngineBrowserOptions.MaxConversionsPerProcess"/>
/// conversions or <see cref="ReportsEngineBrowserOptions.MaxProcessLifetime"/>, and shuts it down
/// cleanly.
/// </summary>
/// <remarks>
/// <para>
/// Every conversion holds a lease on the browser it runs in. A browser that is retired (recycled or
/// crashed) takes no new leases; it closes once its last lease is released, so conversions already
/// running on it finish undisturbed, while new conversions go to its replacement. Only one browser
/// launches at a time; every conversion that needs it waits for the same launch.
/// </para>
/// <para>
/// A failed launch is remembered, for the health check, until a launch succeeds, and it starts a
/// background retry loop: after <see cref="ReportsEngineBrowserOptions.LaunchRetryDelay"/>, doubling
/// up to <see cref="ReportsEngineBrowserOptions.MaxLaunchRetryDelay"/>, until a launch succeeds or
/// shutdown starts. Without it, an engine whose readiness probe fails would get no conversions, and
/// so no launch, ever again. The retries lease the browser like warm-up does, so they never start a
/// second launch beside a conversion's: whichever comes first launches, and the other waits for it.
/// </para>
/// <para>
/// Closing a browser kills its process tree, waits for it to exit, and then deletes its temporary
/// profile directory.
/// </para>
/// </remarks>
internal sealed class BrowserManager : IBrowserProvider, IAsyncDisposable, IDisposable
{
  private readonly ReportsEngineBrowserOptions _options;
  private readonly EngineMetrics _metrics;
  private readonly TimeProvider _timeProvider;
  private readonly ILoggerFactory _loggerFactory;
  private readonly ILogger _logger;
  private readonly Lock _lock = new();
  private readonly List<BrowserInstance> _retired = [];
  private readonly List<Task> _closing = [];
  private readonly CancellationTokenSource _stopping = new();
  private readonly CancellationTokenSource _retryStopping = new();
  private BrowserInstance? _current;
  private Task<BrowserInstance>? _launch;
  private Task? _retrying;
  private string? _launchFailure;
  private int _failedLaunches;
  private TaskCompletionSource? _drained;
  private Task? _shutdown;
  private ITimer? _idleTimer;
  private long _idleSince;
  private int _generation;

  public BrowserManager(
    IOptions<ReportsEngineOptions> options,
    EngineMetrics metrics,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory
  )
  {
    _options = options.Value.Browser;
    _metrics = metrics;
    _timeProvider = timeProvider;
    _loggerFactory = loggerFactory;
    _logger = loggerFactory.CreateLogger<BrowserManager>();
  }

  /// <summary>
  /// The browser new conversions go to, if one is running. Exposed for tests.
  /// </summary>
  internal BrowserInstance? Current
  {
    get
    {
      lock (_lock)
      {
        return _current;
      }
    }
  }

  /// <summary>
  /// The number of browsers launched so far. Exposed for tests.
  /// </summary>
  internal int Launches => Volatile.Read(ref _generation);

  /// <summary>
  /// What the engine knows about its browser right now, for the health check.
  /// </summary>
  internal BrowserStatus GetStatus()
  {
    lock (_lock)
    {
      return new BrowserStatus(
        ShuttingDown: _shutdown is not null,
        Running: _current is { Retired: false, IsAlive: true } current ? current : null,
        LaunchFailure: _launchFailure,
        FailedLaunches: _failedLaunches,
        Retrying: _retrying is not null
      );
    }
  }

  public async ValueTask<IConversionPage> OpenPageAsync(CancellationToken cancellationToken)
  {
    // A browser can die between the lease and the page (killed, crashed, connection dropped). No
    // conversion work has started then, so trying once more on a fresh browser is safe.
    for (var attempt = 1; ; attempt++)
    {
      var browser = await AcquireAsync(cancellationToken);
      EngineActivities.Current?.SetTag(EngineActivities.Tags.BrowserGeneration, browser.Generation);
      try
      {
        var page = await browser.CreatePageAsync(cancellationToken);
        return new LeasedPage(this, browser, page);
      }
      catch (BrowserUnavailableException) when (attempt == 1 && !browser.IsAlive)
      {
        Release(browser);
      }
      catch
      {
        Release(browser);
        throw;
      }
    }
  }

  /// <summary>
  /// Starts the browser now, if it is not running yet.
  /// </summary>
  public async Task WarmUpAsync(CancellationToken cancellationToken)
  {
    var browser = await AcquireAsync(cancellationToken, countConversion: false);
    Release(browser);
  }

  /// <summary>
  /// Stops accepting conversions, waits up to <paramref name="drainTimeout"/> for running ones, then
  /// closes every browser. Later calls wait for the first shutdown.
  /// </summary>
  public Task ShutdownAsync(TimeSpan drainTimeout, CancellationToken cancellationToken)
  {
    lock (_lock)
    {
      _shutdown ??= ShutdownCoreAsync(drainTimeout, cancellationToken);
      return _shutdown;
    }
  }

  public async ValueTask DisposeAsync()
  {
    await ShutdownAsync(_options.ShutdownTimeout, CancellationToken.None);
    _stopping.Dispose();
    _retryStopping.Dispose();
  }

  public void Dispose()
  {
    // Synchronous disposal (ServiceProvider.Dispose) does not wait for running conversions. Run on
    // the thread pool so a captured synchronization context cannot deadlock the wait.
    Task.Run(() => ShutdownAsync(TimeSpan.Zero, CancellationToken.None)).GetAwaiter().GetResult();
  }

  private async ValueTask<BrowserInstance> AcquireAsync(
    CancellationToken cancellationToken,
    bool countConversion = true
  )
  {
    var deaths = 0;
    while (true)
    {
      Task<BrowserInstance> launch;
      lock (_lock)
      {
        if (_shutdown is not null)
        {
          throw new BrowserUnavailableException("The reports engine is shutting down.");
        }

        if (_current is { } current)
        {
          if (TryLeaseLocked(current, countConversion))
          {
            return current;
          }

          if (!current.IsAlive && ++deaths == 2)
          {
            // Two browsers in a row died before this conversion could use them.
            RetireLocked(current);
            throw new BrowserUnavailableException(
              "The browser keeps exiting right after it starts."
            );
          }

          RetireLocked(current);
        }

        launch = _launch ??= LaunchAsync();
      }

      await launch.WaitAsync(cancellationToken);
    }
  }

  private bool TryLeaseLocked(BrowserInstance browser, bool countConversion)
  {
    if (browser.Retired || !browser.IsAlive)
    {
      return false;
    }

    var maxConversions = _options.MaxConversionsPerProcess;
    var lifetime = _options.MaxProcessLifetime;
    if (
      lifetime != Timeout.InfiniteTimeSpan
      && _timeProvider.GetElapsedTime(browser.StartedTimestamp) >= lifetime
    )
    {
      LogMessages.BrowserRecycling(_logger, browser.Generation, "it reached its maximum lifetime");
      EngineActivities.BrowserRecycling(browser.Generation, "max_lifetime");
      _metrics.BrowserRecycled();
      return false;
    }

    browser.InFlight++;
    if (!countConversion)
    {
      return true;
    }

    browser.ConversionsStarted++;
    if (maxConversions > 0 && browser.ConversionsStarted >= maxConversions)
    {
      // This conversion is the browser's last: retire it now so the replacement starts launching
      // while the remaining work drains.
      LogMessages.BrowserRecycling(
        _logger,
        browser.Generation,
        "it served its maximum number of conversions"
      );
      EngineActivities.BrowserRecycling(browser.Generation, "max_conversions");
      _metrics.BrowserRecycled();
      RetireLocked(browser);

      // Nobody waits for this launch yet, so it is not part of this conversion: it traces on its own.
      _launch ??= WithoutExecutionContext(LaunchAsync);
    }

    return true;
  }

  private void RetireLocked(BrowserInstance browser)
  {
    if (_current == browser)
    {
      _current = null;
    }

    if (browser.Retired)
    {
      return;
    }

    browser.Retired = true;
    if (browser.InFlight == 0)
    {
      CloseLocked(browser);
    }
    else
    {
      _retired.Add(browser);
    }
  }

  private void Release(BrowserInstance browser)
  {
    lock (_lock)
    {
      browser.InFlight--;
      if (browser.Retired && browser.InFlight == 0)
      {
        CloseLocked(browser);
      }

      if (browser == _current && browser.InFlight == 0)
      {
        ScheduleIdleCloseLocked();
      }

      if (_drained is not null && InFlightLocked() == 0)
      {
        _drained.TrySetResult();
      }
    }
  }

  private void ScheduleIdleCloseLocked()
  {
    if (_options.IdleTimeout == Timeout.InfiniteTimeSpan || _shutdown is not null)
    {
      return;
    }

    _idleSince = _timeProvider.GetTimestamp();

    // The timer outlives the conversion that creates it; it must not log or trace against it.
    _idleTimer ??= WithoutExecutionContext(() =>
      _timeProvider.CreateTimer(
        _ => CloseIfIdle(),
        null,
        Timeout.InfiniteTimeSpan,
        Timeout.InfiniteTimeSpan
      )
    );
    _idleTimer.Change(_options.IdleTimeout, Timeout.InfiniteTimeSpan);
  }

  private void CloseIfIdle()
  {
    lock (_lock)
    {
      if (_shutdown is not null || _current is not { InFlight: 0 } current)
      {
        // Busy again; the next release starts a new idle period.
        return;
      }

      var idle = _timeProvider.GetElapsedTime(_idleSince);
      if (idle < _options.IdleTimeout)
      {
        _idleTimer?.Change(_options.IdleTimeout - idle, Timeout.InfiniteTimeSpan);
        return;
      }

      LogMessages.BrowserIdleClosing(_logger, current.Generation, _options.IdleTimeout);
      RetireLocked(current);
    }
  }

  private int InFlightLocked()
  {
    var inFlight = _current?.InFlight ?? 0;
    foreach (var browser in _retired)
    {
      inFlight += browser.InFlight;
    }

    return inFlight;
  }

  private async Task<BrowserInstance> LaunchAsync()
  {
    // Leave the caller's lock before doing any work; the caller stores this task first.
    await Task.Yield();

    // A child of the conversion whose page needed the browser; a trace of its own otherwise.
    var generation = Interlocked.Increment(ref _generation);
    int attempt;
    lock (_lock)
    {
      attempt = _failedLaunches + 1;
    }

    using var activity = EngineActivities.StartBrowserLaunch(generation, attempt);

    BrowserInstance browser;
    try
    {
      browser = await BrowserInstance.LaunchAsync(
        _options,
        generation,
        _timeProvider,
        _loggerFactory,
        _stopping.Token
      );
    }
    catch (Exception exception)
    {
      var failure =
        exception as BrowserUnavailableException
        ?? new BrowserUnavailableException(
          exception is OperationCanceledException
            ? "The reports engine is shutting down."
            : $"The browser could not be started: {exception.Message}",
          exception
        );

      // Before the retry loop can announce the next attempt.
      EngineActivities.Failed(activity, exception);
      activity?.AddException(exception);
      LogMessages.BrowserLaunchFailed(_logger, exception, generation, attempt);
      lock (_lock)
      {
        _launch = null;

        // A launch that shutdown cancelled says nothing about whether the browser can start.
        if (_shutdown is null && exception is not OperationCanceledException)
        {
          _failedLaunches++;
          _launchFailure = failure.Message;
          ScheduleRetryLocked();
        }
      }

      if (exception is BrowserUnavailableException)
      {
        throw;
      }

      throw failure;
    }

    activity?.SetTag(EngineActivities.Tags.BrowserProcessId, browser.ProcessId);
    int failedLaunches;
    lock (_lock)
    {
      _launch = null;
      if (_shutdown is not null)
      {
        browser.Retired = true;
        CloseLocked(browser);
        BrowserUnavailableException shuttingDown = new("The reports engine is shutting down.");
        EngineActivities.Failed(activity, shuttingDown);
        throw shuttingDown;
      }

      _current = browser;
      failedLaunches = _failedLaunches;
      _failedLaunches = 0;
      _launchFailure = null;
    }

    _metrics.BrowserLaunched();
    if (failedLaunches > 0)
    {
      LogMessages.BrowserLaunchRecovered(_logger, generation, failedLaunches);
    }

    // The browser outlives this launch: its crash is not part of whatever trace launched it.
    _ = WithoutExecutionContext(() =>
      browser.Terminated.ContinueWith(
        _ => OnTerminated(browser),
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default
      )
    );
    return browser;
  }

  private void ScheduleRetryLocked()
  {
    if (_shutdown is not null || _retrying is not null)
    {
      return;
    }

    // The retries outlive the launch that failed: they trace and log on their own.
    _retrying = WithoutExecutionContext(RetryLaunchAsync);
  }

  /// <summary>
  /// Retries a failed launch until one succeeds (this loop's or a conversion's) or shutdown starts.
  /// </summary>
  private async Task RetryLaunchAsync()
  {
    // Leave the caller's lock before doing any work; the caller stores this task first.
    await Task.Yield();

    var stopping = _retryStopping.Token;
    var delay = _options.LaunchRetryDelay;
    try
    {
      while (true)
      {
        int attempt;
        lock (_lock)
        {
          if (TryEndRetriesLocked())
          {
            return;
          }

          attempt = _failedLaunches + 1;
        }

        LogMessages.BrowserLaunchRetryScheduled(_logger, delay, attempt);
        await Task.Delay(delay, _timeProvider, stopping);
        lock (_lock)
        {
          // A conversion may have launched the browser in the meantime.
          if (TryEndRetriesLocked())
          {
            return;
          }
        }

        try
        {
          // Leased like warm-up: a launch already under way (a conversion's) is waited for, not
          // duplicated.
          Release(await AcquireAsync(stopping, countConversion: false));
          delay = _options.LaunchRetryDelay;
        }
        catch (BrowserUnavailableException)
        {
          // The launch logged and traced its failure.
          delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxLaunchRetryDelay.Ticks));
        }
      }
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
      // Shutdown started.
      lock (_lock)
      {
        _retrying = null;
      }
    }
  }

  /// <summary>
  /// Ends the retry loop, in the same lock that decides it, unless the last launch failed and the
  /// engine is not shutting down. A launch that fails after this sees no loop and starts a new one.
  /// </summary>
  private bool TryEndRetriesLocked()
  {
    if (_shutdown is null && _launchFailure is not null)
    {
      return false;
    }

    _retrying = null;
    return true;
  }

  /// <summary>
  /// Starts background work without the caller's execution context, so work that outlives a
  /// conversion (a launch nobody waits for yet, a timer, a crash watcher) neither runs in its trace
  /// nor carries its logging scopes.
  /// </summary>
  private static T WithoutExecutionContext<T>(Func<T> start)
  {
    if (ExecutionContext.IsFlowSuppressed())
    {
      return start();
    }

    using (ExecutionContext.SuppressFlow())
    {
      return start();
    }
  }

  private void OnTerminated(BrowserInstance browser)
  {
    lock (_lock)
    {
      if (browser.CloseStarted)
      {
        return;
      }

      // Conversions still running on it fail on their own: their commands fail as the
      // connection closes. Kill whatever is left of the process right away.
      LogMessages.BrowserTerminated(_logger, browser.Generation, browser.ProcessId);
      _metrics.BrowserCrashed();
      RetireLocked(browser);
      if (!browser.CloseStarted)
      {
        _retired.Remove(browser);
        CloseLocked(browser);
      }
    }
  }

  private void CloseLocked(BrowserInstance browser)
  {
    if (browser.CloseStarted)
    {
      return;
    }

    browser.CloseStarted = true;
    _retired.Remove(browser);
    var closing = CloseAsync(browser);
    _closing.Add(closing);
    _ = closing.ContinueWith(
      task =>
      {
        lock (_lock)
        {
          _closing.Remove(task);
        }
      },
      CancellationToken.None,
      TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default
    );
  }

  private async Task CloseAsync(BrowserInstance browser)
  {
    await Task.Yield();
    try
    {
      await browser.DisposeAsync();
      LogMessages.BrowserClosed(_logger, browser.Generation, browser.ProcessId);
    }
    catch (Exception exception)
    {
      LogMessages.BrowserCloseFailed(_logger, exception, browser.Generation);
    }
  }

  private async Task ShutdownCoreAsync(TimeSpan drainTimeout, CancellationToken cancellationToken)
  {
    await Task.Yield();

    // Nothing may start a browser from now on: stop retrying failed launches at once, even mid-wait.
    await _retryStopping.CancelAsync();
    Task? retrying;
    lock (_lock)
    {
      retrying = _retrying;
    }

    if (retrying is not null)
    {
      await retrying.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    Task drained;
    lock (_lock)
    {
      if (InFlightLocked() == 0)
      {
        drained = Task.CompletedTask;
      }
      else
      {
        _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        drained = _drained.Task;
      }
    }

    try
    {
      await drained.WaitAsync(drainTimeout, cancellationToken);
    }
    catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
    {
      int inFlight;
      lock (_lock)
      {
        inFlight = InFlightLocked();
      }

      LogMessages.ShutdownDrainTimedOut(_logger, inFlight);
    }

    await _stopping.CancelAsync();

    Task<BrowserInstance>? launch;
    lock (_lock)
    {
      _idleTimer?.Dispose();
    }

    lock (_lock)
    {
      launch = _launch;
    }

    if (launch is not null)
    {
      try
      {
        // A launch that finishes now closes its own browser, because shutdown has started.
        await launch;
      }
      catch (BrowserUnavailableException) { }
    }

    Task[] closing;
    lock (_lock)
    {
      if (_current is { } current)
      {
        _current = null;
        current.Retired = true;
        CloseLocked(current);
      }

      foreach (var browser in _retired.ToArray())
      {
        CloseLocked(browser);
      }

      closing = [.. _closing];
    }

    await Task.WhenAll(closing);
  }

  /// <summary>
  /// A conversion page that releases its browser lease when disposed.
  /// </summary>
  private sealed class LeasedPage(
    BrowserManager manager,
    BrowserInstance browser,
    IConversionPage page
  ) : IConversionPage
  {
    private bool _disposed;

    public Task EnableSignalAsync(string signalName, CancellationToken cancellationToken) =>
      page.EnableSignalAsync(signalName, cancellationToken);

    public Task SetContentAsync(string html, CancellationToken cancellationToken) =>
      page.SetContentAsync(html, cancellationToken);

    public Task<bool> WaitForSignalAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
      page.WaitForSignalAsync(timeout, cancellationToken);

    public Task WaitForLoadAsync(CancellationToken cancellationToken) =>
      page.WaitForLoadAsync(cancellationToken);

    public Task<long> PrintToPdfAsync(
      PdfOptions options,
      Stream destination,
      CancellationToken cancellationToken
    ) => page.PrintToPdfAsync(options, destination, cancellationToken);

    public async ValueTask DisposeAsync()
    {
      if (Interlocked.Exchange(ref _disposed, true))
      {
        return;
      }

      try
      {
        await page.DisposeAsync();
      }
      finally
      {
        manager.Release(browser);
      }
    }
  }
}
