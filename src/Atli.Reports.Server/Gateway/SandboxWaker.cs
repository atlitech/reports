using System.Diagnostics;
using Atli.Reports.Hosting.Sandboxes;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Wakes suspended renderer sandboxes. A not-running answer is only a claim: it reaches the gateway
/// through the renderer's port, so a compromised renderer can send it too. Before resuming, the
/// waker asks the Sandboxes data plane for the sandbox's state, and resumes only a sandbox that
/// exists and is not running.
/// </summary>
/// <remarks>
/// <para>
/// Requests that find the same sandbox asleep share one check: only one is in flight per sandbox,
/// it runs apart from the request that starts it (see <see cref="SharedWork"/>), and every request
/// waits for it. A state read is reused for <see cref="StateLifetime"/>, and a sandbox is resumed at
/// most once per <see cref="MinResumeInterval"/>. A request whose not-running answer predates the
/// sandbox's last resume needs no check of its own.
/// </para>
/// <para>
/// The gateway's identity needs only <c>sandboxes/read</c> and <c>sandboxes/resume/action</c> on the
/// renderers' sandbox group; see docs/hosted-renderers.md.
/// </para>
/// </remarks>
internal sealed partial class SandboxWaker(
  ISandboxesClient sandboxes,
  GatewayOptions options,
  TimeProvider timeProvider,
  ILogger<SandboxWaker> logger
)
{
  /// <summary>The named <see cref="HttpClient"/> the Sandboxes data-plane client sends with.</summary>
  public const string HttpClientName = "Atli.Reports.Gateway.Sandboxes";

  /// <summary>How long a sandbox's state, as the data plane reported it, is reused.</summary>
  private static readonly TimeSpan StateLifetime = TimeSpan.FromSeconds(2);

  /// <summary>The shortest time between two resume calls for one sandbox.</summary>
  private static readonly TimeSpan MinResumeInterval = TimeSpan.FromSeconds(5);

  private readonly Lock _gate = new();
  private readonly Dictionary<string, SandboxEntry> _sandboxes = new(StringComparer.Ordinal);

  /// <summary>
  /// Checks <paramref name="sandboxId"/> and resumes it if it is not running, or joins the check
  /// already in flight for it. Failures are logged, never thrown, so a request can keep trying
  /// within its wake window. <paramref name="cancellationToken"/> stops this request's wait, not the
  /// shared check, which <see cref="GatewayWakeOptions.Timeout"/> bounds.
  /// </summary>
  /// <param name="tenantId">The renderer's tenant, for the logs.</param>
  /// <param name="sandboxId">The sandbox the not-running answer was about.</param>
  /// <param name="sentAt">
  /// When the request that got the not-running answer was sent. If a resume finished since, that
  /// answer may be stale: the sandbox counts as resumed without a check.
  /// </param>
  /// <param name="cancellationToken">Stops this request's wait.</param>
  public Task<WakeResult> WakeAsync(
    string tenantId,
    string sandboxId,
    long sentAt,
    CancellationToken cancellationToken
  )
  {
    TaskCompletionSource<WakeResult> started;
    SandboxEntry entry;
    lock (_gate)
    {
      if (!_sandboxes.TryGetValue(sandboxId, out var known))
      {
        known = new SandboxEntry();
        _sandboxes[sandboxId] = known;
      }

      entry = known;
      if (entry.ResumedAt is { } resumedAt && resumedAt >= sentAt)
      {
        return Task.FromResult(WakeResult.Resumed);
      }

      if (entry.Checking is { } inFlight)
      {
        return inFlight.WaitAsync(cancellationToken);
      }

      // A running sandbox answered through its port that it is not running: nothing to resume.
      if (Fresh(entry.StateAt) && entry.State == SandboxStates.Running)
      {
        return Task.FromResult(WakeResult.Running);
      }

      started = new TaskCompletionSource<WakeResult>(
        TaskCreationOptions.RunContinuationsAsynchronously
      );
      entry.Checking = started.Task;
    }

    // Not awaited: the check belongs to every request that joins it, and sets their result itself.
    _ = SharedWork.Run(() => CheckAsync(tenantId, sandboxId, entry, started));
    return started.Task.WaitAsync(cancellationToken);
  }

  private async Task CheckAsync(
    string tenantId,
    string sandboxId,
    SandboxEntry entry,
    TaskCompletionSource<WakeResult> result
  )
  {
    var outcome = WakeResult.Pending;
    try
    {
      using var timeout = new CancellationTokenSource(options.Wake.Timeout, timeProvider);
      var (read, state) = await ReadStateAsync(tenantId, sandboxId, entry, timeout.Token);
      if (read)
      {
        outcome = state switch
        {
          null => WakeResult.Missing,
          SandboxStates.Running => WakeResult.Running,
          _ => await ResumeAsync(tenantId, sandboxId, entry, timeout.Token),
        };
      }
    }
    finally
    {
      lock (_gate)
      {
        entry.Checking = null;
      }

      result.SetResult(outcome);
    }
  }

  /// <summary>
  /// The sandbox's state, reused while fresh, with <see langword="null"/> for a sandbox that does
  /// not exist; or <c>Read</c> <see langword="false"/> when the data plane failed, which is logged.
  /// </summary>
  private async Task<(bool Read, string? State)> ReadStateAsync(
    string tenantId,
    string sandboxId,
    SandboxEntry entry,
    CancellationToken cancellationToken
  )
  {
    lock (_gate)
    {
      if (Fresh(entry.StateAt))
      {
        return (true, entry.State);
      }
    }

    SandboxView? sandbox;
    try
    {
      sandbox = await sandboxes.GetAsync(sandboxId, cancellationToken);
    }
    catch (Exception exception)
    {
      LogReadFailed(logger, tenantId, sandboxId, exception);
      return (false, null);
    }

    lock (_gate)
    {
      entry.State = sandbox?.State;
      entry.StateAt = timeProvider.GetTimestamp();
    }

    return (true, sandbox?.State);
  }

  private async Task<WakeResult> ResumeAsync(
    string tenantId,
    string sandboxId,
    SandboxEntry entry,
    CancellationToken cancellationToken
  )
  {
    lock (_gate)
    {
      if (
        entry.ResumeStartedAt is { } resumeStartedAt
        && timeProvider.GetElapsedTime(resumeStartedAt) < MinResumeInterval
      )
      {
        return WakeResult.Pending;
      }

      entry.ResumeStartedAt = timeProvider.GetTimestamp();
    }

    var started = Stopwatch.GetTimestamp();
    SandboxView sandbox;
    try
    {
      LogResuming(logger, tenantId, sandboxId);
      sandbox = await sandboxes.ResumeAsync(sandboxId, cancellationToken);
    }
    catch (Exception exception)
    {
      LogResumeFailed(logger, tenantId, sandboxId, exception);
      return WakeResult.Pending;
    }

    if (logger.IsEnabled(LogLevel.Information))
    {
      var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
      LogResumed(logger, tenantId, sandboxId, sandbox.State, elapsedMilliseconds);
    }

    lock (_gate)
    {
      var now = timeProvider.GetTimestamp();
      entry.ResumedAt = now;
      entry.State = sandbox.State;
      entry.StateAt = now;
    }

    return WakeResult.Resumed;
  }

  private bool Fresh(long? at) =>
    at is { } value && timeProvider.GetElapsedTime(value) < StateLifetime;

  [LoggerMessage(
    EventId = 60,
    Level = LogLevel.Information,
    Message = "Resuming sandbox {SandboxId}, the renderer of tenant {TenantId}."
  )]
  private static partial void LogResuming(ILogger logger, string tenantId, string sandboxId);

  [LoggerMessage(
    EventId = 61,
    Level = LogLevel.Information,
    Message = "Sandbox {SandboxId}, the renderer of tenant {TenantId}, resumed ({State}) in {ElapsedMilliseconds} ms."
  )]
  private static partial void LogResumed(
    ILogger logger,
    string tenantId,
    string sandboxId,
    string state,
    double elapsedMilliseconds
  );

  [LoggerMessage(
    EventId = 62,
    Level = LogLevel.Warning,
    Message = "Resuming sandbox {SandboxId}, the renderer of tenant {TenantId}, failed."
  )]
  private static partial void LogResumeFailed(
    ILogger logger,
    string tenantId,
    string sandboxId,
    Exception exception
  );

  [LoggerMessage(
    EventId = 63,
    Level = LogLevel.Warning,
    Message = "Reading the state of sandbox {SandboxId}, the renderer of tenant {TenantId}, failed."
  )]
  private static partial void LogReadFailed(
    ILogger logger,
    string tenantId,
    string sandboxId,
    Exception exception
  );

  /// <summary>What the gateway knows about one sandbox. Guarded by <see cref="_gate"/>.</summary>
  private sealed class SandboxEntry
  {
    /// <summary>The check in flight, which requests join.</summary>
    public Task<WakeResult>? Checking { get; set; }

    /// <summary>The state the data plane last reported; <see langword="null"/> for no sandbox.</summary>
    public string? State { get; set; }

    /// <summary>When <see cref="State"/> was read, as a <see cref="TimeProvider"/> timestamp.</summary>
    public long? StateAt { get; set; }

    /// <summary>When the last resume call started.</summary>
    public long? ResumeStartedAt { get; set; }

    /// <summary>When the last resume succeeded.</summary>
    public long? ResumedAt { get; set; }
  }
}

/// <summary>What a request does after a sandbox's not-running answer.</summary>
internal enum WakeResult
{
  /// <summary>The sandbox was resumed (or was, after the answer): send the conversion again.</summary>
  Resumed,

  /// <summary>
  /// The data plane reports the sandbox running, so the answer did not come from the platform's
  /// proxy: a renderer failure, not a reason to resume or resend.
  /// </summary>
  Running,

  /// <summary>The sandbox does not exist: the tenant's record is stale.</summary>
  Missing,

  /// <summary>
  /// The check or the resume failed, or the sandbox was resumed too recently to resume again: wait,
  /// then send the conversion again within the wake window.
  /// </summary>
  Pending,
}
