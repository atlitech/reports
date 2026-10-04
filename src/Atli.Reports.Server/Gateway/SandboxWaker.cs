using System.Diagnostics;
using Atli.Reports.Hosting.Sandboxes;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Resumes suspended renderer sandboxes. Requests that find the same sandbox asleep share one
/// resume call: only one is in flight per sandbox, and every request waits for it. A request whose
/// not-running answer predates the sandbox's last resume needs none of its own.
/// </summary>
/// <remarks>
/// The gateway's identity needs only <c>sandboxes/read</c> and <c>sandboxes/resume/action</c> on the
/// renderers' sandbox group; see docs/hosted-renderers.md.
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

  private readonly Lock _gate = new();
  private readonly Dictionary<string, Task<bool>> _resuming = new(StringComparer.Ordinal);

  /// <summary>When each sandbox last resumed, as a <see cref="TimeProvider"/> timestamp.</summary>
  private readonly Dictionary<string, long> _resumedAt = new(StringComparer.Ordinal);

  /// <summary>
  /// Resumes <paramref name="sandboxId"/>, or joins the resume already in flight for it. Returns
  /// whether the platform accepted the resume; failures are logged, never thrown, so a request can
  /// keep retrying within its wake window. <paramref name="cancellationToken"/> stops this request's
  /// wait, not the shared call, which <see cref="GatewayWakeOptions.Timeout"/> bounds.
  /// </summary>
  /// <param name="tenantId">The renderer's tenant, for the logs.</param>
  /// <param name="sandboxId">The sandbox to resume.</param>
  /// <param name="sentAt">
  /// When the request that found the sandbox not running was sent. If a resume finished since, that
  /// answer may be stale: the sandbox counts as resumed without another call.
  /// </param>
  /// <param name="cancellationToken">Stops this request's wait.</param>
  public Task<bool> ResumeAsync(
    string tenantId,
    string sandboxId,
    long sentAt,
    CancellationToken cancellationToken
  )
  {
    TaskCompletionSource<bool> started;
    lock (_gate)
    {
      if (_resumedAt.TryGetValue(sandboxId, out var resumedAt) && resumedAt >= sentAt)
      {
        return Task.FromResult(true);
      }

      if (_resuming.TryGetValue(sandboxId, out var inFlight))
      {
        return inFlight.WaitAsync(cancellationToken);
      }

      started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      _resuming[sandboxId] = started.Task;
    }

    // Started outside the lock, so a client that completes synchronously cannot finish (and leave
    // the dictionary) before its entry is added.
    _ = RunAsync(tenantId, sandboxId, started);
    return started.Task.WaitAsync(cancellationToken);
  }

  private async Task RunAsync(string tenantId, string sandboxId, TaskCompletionSource<bool> result)
  {
    var resumed = false;
    var started = Stopwatch.GetTimestamp();
    try
    {
      LogResuming(logger, tenantId, sandboxId);
      using var timeout = new CancellationTokenSource(options.Wake.Timeout);
      var sandbox = await sandboxes.ResumeAsync(sandboxId, timeout.Token);
      resumed = true;
      if (logger.IsEnabled(LogLevel.Information))
      {
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogResumed(logger, tenantId, sandboxId, sandbox.State, elapsedMilliseconds);
      }
    }
    catch (Exception exception)
    {
      LogResumeFailed(logger, tenantId, sandboxId, exception);
    }
    finally
    {
      lock (_gate)
      {
        _resuming.Remove(sandboxId);
        if (resumed)
        {
          _resumedAt[sandboxId] = timeProvider.GetTimestamp();
        }
      }

      result.SetResult(resumed);
    }
  }

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
}
