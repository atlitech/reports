using Atli.Reports.Engine.Chromium.Protocol.Messages;

namespace Atli.Reports.Engine.Chromium.Connection;

/// <summary>
/// A flat-mode DevTools session attached to one page target, sharing the browser's connection.
/// </summary>
internal sealed class DevToolsSession : IDisposable
{
  private readonly DevToolsConnection _connection;
  private readonly TaskCompletionSource _terminated = new(
    TaskCreationOptions.RunContinuationsAsynchronously
  );

  internal DevToolsSession(DevToolsConnection connection, string sessionId, string targetId)
  {
    _connection = connection;
    SessionId = sessionId;
    TargetId = targetId;
  }

  /// <summary>
  /// The DevTools session id.
  /// </summary>
  public string SessionId { get; }

  /// <summary>
  /// The page target the session is attached to. For a page, this is also its main frame id.
  /// </summary>
  public string TargetId { get; }

  /// <summary>
  /// Faults when the page crashes, detaches, or the connection closes.
  /// </summary>
  public Task Terminated => _terminated.Task;

  /// <summary>
  /// Raised, on the connection's receive loop, for the session's events.
  /// </summary>
  public event DevToolsEventHandler? EventReceived;

  /// <inheritdoc cref="DevToolsConnection.SendAsync"/>
  public async Task<DevToolsReply> SendAsync(
    DevToolsMessage message,
    CancellationToken cancellationToken,
    TimeSpan? timeout = null
  )
  {
    if (_terminated.Task.IsCompleted)
    {
      await _terminated.Task;
    }

    try
    {
      return await _connection.SendAsync(message, SessionId, cancellationToken, timeout);
    }
    catch when (_terminated.Task.IsCompleted)
    {
      // Preserve the reason if termination raced command registration.
      await _terminated.Task;
      throw;
    }
  }

  /// <summary>Stops pending and future commands, preserving the policy or lifecycle failure.</summary>
  internal void Fail(Exception reason) => _connection.TerminateSession(SessionId, reason);

  /// <summary>
  /// Sends <paramref name="message"/> and discards its result.
  /// </summary>
  public async Task ExecuteAsync(
    DevToolsMessage message,
    CancellationToken cancellationToken,
    TimeSpan? timeout = null
  )
  {
    using var reply = await SendAsync(message, cancellationToken, timeout);
  }

  /// <summary>
  /// Sends <paramref name="message"/> without waiting for its reply. Failures are ignored.
  /// </summary>
  public void Post(DevToolsMessage message) => _connection.Post(message, SessionId);

  public void Dispose() => _connection.DetachSession(this);

  internal void OnEvent(string method, ReadOnlySpan<byte> parameters)
  {
    if (method is "Inspector.targetCrashed" or "Inspector.detached")
    {
      _connection.TerminateSession(SessionId, new TargetCrashedException());
      return;
    }

    EventReceived?.Invoke(method, parameters);
  }

  internal void Terminate(Exception reason)
  {
    if (_terminated.TrySetException(reason))
    {
      // Observe the exception: nobody may be waiting on Terminated.
      _ = _terminated.Task.Exception;
    }
  }
}
