using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atli.Reports.Engine.Chromium.Protocol;
using Atli.Reports.Engine.Chromium.Protocol.Messages;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Chromium.Connection;

/// <summary>
/// Handles a DevTools event. <paramref name="parameters"/> is the UTF-8 JSON of the event's
/// <c>params</c> object and is only valid during the call.
/// </summary>
internal delegate void DevToolsEventHandler(string method, ReadOnlySpan<byte> parameters);

/// <summary>
/// A WebSocket connection to a browser's DevTools endpoint, multiplexing the browser itself and any
/// number of page sessions attached in flat mode.
/// </summary>
/// <remarks>
/// <para>
/// Commands are written straight to UTF-8 JSON and sent under a lock, so the socket never has two
/// sends in flight. Every command is registered before it is sent, so a reply that arrives before
/// the sender awaits it is never lost. Replies hand the caller only their <c>result</c> object, in a
/// pooled buffer the caller disposes; error replies throw <see cref="DevToolsProtocolException"/>.
/// </para>
/// <para>
/// A single receive loop reassembles messages of any size from their WebSocket frames and
/// dispatches them. Events run on that loop, so handlers must be quick and must not block.
/// When the socket closes, every pending command and every session fails with
/// <see cref="BrowserConnectionClosedException"/> and <see cref="Closed"/> completes.
/// </para>
/// </remarks>
internal sealed class DevToolsConnection : IAsyncDisposable
{
  private const int InitialReceiveBufferSize = 64 * 1024;

  /// <summary>
  /// The largest message the connection accepts. A bigger one means something is badly wrong, so
  /// the connection closes instead of growing its buffer without bound.
  /// </summary>
  private const int MaxMessageSize = 512 * 1024 * 1024;

  // Relaxed escaping keeps HTML ('<', '>', '&', '\'') unescaped, so large documents do not grow
  // sixfold on the wire. The JSON is never embedded in HTML, so the relaxed encoder is safe here.
  private static readonly JsonWriterOptions WriterOptions = new()
  {
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    SkipValidation = true,
  };

  private readonly WebSocket _socket;
  private readonly TimeSpan _commandTimeout;
  private readonly ILogger _logger;
  private readonly SemaphoreSlim _sendLock = new(1, 1);
  private readonly ConcurrentDictionary<int, PendingCommand> _pending = new();
  private readonly ConcurrentDictionary<string, DevToolsSession> _sessions = new(
    StringComparer.Ordinal
  );
  private readonly CancellationTokenSource _stopping = new();
  private readonly TaskCompletionSource _closed = new(
    TaskCreationOptions.RunContinuationsAsynchronously
  );
  private readonly Task _receiveLoop;
  private Exception? _closeReason;
  private int _lastId;
  private int _disposed;

  private DevToolsConnection(WebSocket socket, Uri uri, TimeSpan commandTimeout, ILogger logger)
  {
    _socket = socket;
    Uri = uri;
    _commandTimeout = commandTimeout;
    _logger = logger;
    _receiveLoop = Task.Run(ReceiveLoopAsync);
  }

  /// <summary>
  /// The DevTools endpoint.
  /// </summary>
  public Uri Uri { get; }

  /// <summary>
  /// Completes when the connection has closed, for whatever reason.
  /// </summary>
  public Task Closed => _closed.Task;

  /// <summary>
  /// Whether the connection can still send commands.
  /// </summary>
  public bool IsOpen => Volatile.Read(ref _closeReason) is null;

  /// <summary>
  /// The number of commands waiting for a reply. Exposed for tests.
  /// </summary>
  internal int PendingCount => _pending.Count;

  /// <summary>
  /// Raised, on the receive loop, for events that do not belong to a session.
  /// </summary>
  public event DevToolsEventHandler? EventReceived;

  /// <summary>
  /// Opens a connection to <paramref name="uri"/>.
  /// </summary>
  public static async Task<DevToolsConnection> ConnectAsync(
    Uri uri,
    TimeSpan commandTimeout,
    ILogger logger,
    CancellationToken cancellationToken
  )
  {
    ClientWebSocket socket = new();
    // Loopback connection to a process the engine owns: process exit is detected directly.
    socket.Options.KeepAliveInterval = TimeSpan.Zero;
    try
    {
      await socket.ConnectAsync(uri, cancellationToken);
    }
    catch
    {
      socket.Dispose();
      throw;
    }

    return new DevToolsConnection(socket, uri, commandTimeout, logger);
  }

  /// <summary>
  /// Wraps an already connected socket. Used by tests to drive the connection without a browser.
  /// </summary>
  internal static DevToolsConnection Create(
    WebSocket socket,
    Uri uri,
    TimeSpan commandTimeout,
    ILogger logger
  ) => new(socket, uri, commandTimeout, logger);

  /// <summary>
  /// Sends <paramref name="message"/> and waits for its result.
  /// </summary>
  /// <param name="message">The command.</param>
  /// <param name="sessionId">The session to send it to, or <see langword="null"/> for the browser.</param>
  /// <param name="cancellationToken">Stops waiting for the reply.</param>
  /// <param name="timeout">Overrides the connection's command timeout.</param>
  /// <returns>The command's result, which the caller must dispose.</returns>
  /// <exception cref="DevToolsProtocolException">The browser answered with an error.</exception>
  /// <exception cref="DevToolsTimeoutException">The browser did not answer in time.</exception>
  /// <exception cref="BrowserConnectionClosedException">The connection is closed or closed meanwhile.</exception>
  /// <exception cref="TargetCrashedException">The session's page crashed or detached meanwhile.</exception>
  public async Task<DevToolsReply> SendAsync(
    DevToolsMessage message,
    string? sessionId,
    CancellationToken cancellationToken,
    TimeSpan? timeout = null
  )
  {
    ThrowIfClosed();
    cancellationToken.ThrowIfCancellationRequested();

    var id = Interlocked.Increment(ref _lastId);
    PendingCommand pending = new(message.Method, sessionId);
    _pending[id] = pending;

    // Close() may have drained the pending commands between the check above and the registration.
    if (Volatile.Read(ref _closeReason) is { } closeReason)
    {
      _pending.TryRemove(id, out _);
      throw AsClosedException(closeReason);
    }

    if (sessionId is not null && !_sessions.ContainsKey(sessionId))
    {
      _pending.TryRemove(id, out _);
      throw new TargetCrashedException();
    }

    var effectiveTimeout = timeout ?? _commandTimeout;
    try
    {
      await SendFrameAsync(message, id, sessionId, cancellationToken);
      return await pending.Task.WaitAsync(effectiveTimeout, cancellationToken);
    }
    catch (TimeoutException)
    {
      Abandon(id, pending);
      throw new DevToolsTimeoutException(message.Method, effectiveTimeout);
    }
    catch
    {
      Abandon(id, pending);
      throw;
    }
  }

  /// <summary>
  /// Sends <paramref name="message"/> without waiting for its reply. Failures are ignored.
  /// </summary>
  public void Post(DevToolsMessage message, string? sessionId)
  {
    if (!IsOpen)
    {
      return;
    }

    _ = SendAsync(message, sessionId, CancellationToken.None)
      .ContinueWith(
        static task =>
        {
          if (task.IsCompletedSuccessfully)
          {
            task.Result.Dispose();
          }
          else
          {
            _ = task.Exception;
          }
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default
      );
  }

  /// <summary>
  /// Registers a flat-mode session so its events and failures are routed to it.
  /// </summary>
  public DevToolsSession AttachSession(string sessionId, string targetId)
  {
    DevToolsSession session = new(this, sessionId, targetId);
    _sessions[sessionId] = session;
    if (Volatile.Read(ref _closeReason) is { } closeReason)
    {
      _sessions.TryRemove(sessionId, out _);
      session.Terminate(AsClosedException(closeReason));
    }

    return session;
  }

  /// <summary>
  /// Stops routing events to <paramref name="session"/>.
  /// </summary>
  internal void DetachSession(DevToolsSession session) =>
    _sessions.TryRemove(new KeyValuePair<string, DevToolsSession>(session.SessionId, session));

  /// <summary>
  /// Fails <paramref name="sessionId"/>'s session and every command waiting on it.
  /// </summary>
  internal void TerminateSession(string sessionId, Exception reason)
  {
    if (_sessions.TryRemove(sessionId, out var session))
    {
      session.Terminate(reason);
    }

    foreach (var (id, pending) in _pending)
    {
      if (pending.SessionId == sessionId && _pending.TryRemove(id, out _))
      {
        pending.TrySetException(reason);
      }
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref _disposed, 1) == 1)
    {
      return;
    }

    Close(new BrowserConnectionClosedException("The DevTools connection was closed."));
    await _stopping.CancelAsync();
    _socket.Abort();

    try
    {
      await _receiveLoop;
    }
    catch (Exception exception)
    {
      LogMessages.ReceiveLoopFailed(_logger, exception, Uri);
    }

    _socket.Dispose();
    _stopping.Dispose();
  }

  private async Task SendFrameAsync(
    DevToolsMessage message,
    int id,
    string? sessionId,
    CancellationToken cancellationToken
  )
  {
    using PooledBufferWriter buffer = new();
    using (Utf8JsonWriter writer = new(buffer, WriterOptions))
    {
      message.WriteTo(writer, id, sessionId);
    }

    await _sendLock.WaitAsync(cancellationToken);
    try
    {
      ThrowIfClosed();

      // Never cancel a send halfway: a canceled ClientWebSocket send aborts the whole socket. Only
      // closing the connection stops a send in progress.
      await _socket.SendAsync(
        buffer.WrittenMemory,
        WebSocketMessageType.Text,
        endOfMessage: true,
        _stopping.Token
      );
    }
    catch (Exception exception)
      when (exception is WebSocketException or ObjectDisposedException
        || (exception is OperationCanceledException && _stopping.IsCancellationRequested)
      )
    {
      var reason = new BrowserConnectionClosedException(
        "The DevTools connection failed while sending a command.",
        exception
      );
      Close(reason);
      throw reason;
    }
    finally
    {
      _sendLock.Release();
    }
  }

  private async Task ReceiveLoopAsync()
  {
    var buffer = ArrayPool<byte>.Shared.Rent(InitialReceiveBufferSize);
    var count = 0;
    Exception reason = new BrowserConnectionClosedException("The DevTools connection was closed.");

    try
    {
      while (true)
      {
        if (count == buffer.Length)
        {
          if (buffer.Length >= MaxMessageSize)
          {
            throw new InvalidDataException(
              $"A DevTools message exceeded {MaxMessageSize / (1024 * 1024)} MB."
            );
          }

          var grown = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MaxMessageSize));
          buffer.AsSpan(0, count).CopyTo(grown);
          ArrayPool<byte>.Shared.Return(buffer);
          buffer = grown;
        }

        var received = await _socket.ReceiveAsync(buffer.AsMemory(count), _stopping.Token);
        if (received.MessageType == WebSocketMessageType.Close)
        {
          reason = new BrowserConnectionClosedException(
            "The browser closed the DevTools connection."
          );
          break;
        }

        count += received.Count;
        if (!received.EndOfMessage)
        {
          continue;
        }

        try
        {
          Dispatch(buffer.AsSpan(0, count));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
          // One malformed message must not take the whole connection down.
          LogMessages.MalformedDevToolsMessage(_logger, exception, Uri);
        }

        count = 0;
        if (buffer.Length > InitialReceiveBufferSize * 16)
        {
          // Do not hold on to the buffer grown for one large message (a big PDF chunk, say).
          ArrayPool<byte>.Shared.Return(buffer);
          buffer = ArrayPool<byte>.Shared.Rent(InitialReceiveBufferSize);
        }
      }
    }
    catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
    {
      // Disposed: the reason set by DisposeAsync stands.
    }
    catch (Exception exception)
    {
      reason = new BrowserConnectionClosedException(
        "The DevTools connection to the browser was lost.",
        exception
      );
      LogMessages.DevToolsConnectionLost(_logger, exception, Uri);
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(buffer);
      Close(reason);
    }
  }

  private void Dispatch(ReadOnlySpan<byte> message)
  {
    Utf8JsonReader reader = new(message);
    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
    {
      return;
    }

    int? id = null;
    string? method = null;
    string? sessionId = null;
    Range result = default;
    Range parameters = default;
    Range error = default;
    var hasResult = false;
    var hasParameters = false;
    var hasError = false;

    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
    {
      if (reader.ValueTextEquals("id"u8))
      {
        reader.Read();
        id = reader.GetInt32();
      }
      else if (reader.ValueTextEquals("method"u8))
      {
        reader.Read();
        method = reader.GetString();
      }
      else if (reader.ValueTextEquals("sessionId"u8))
      {
        reader.Read();
        sessionId = reader.GetString();
      }
      else if (reader.ValueTextEquals("result"u8))
      {
        result = ReadValueRange(ref reader);
        hasResult = true;
      }
      else if (reader.ValueTextEquals("params"u8))
      {
        parameters = ReadValueRange(ref reader);
        hasParameters = true;
      }
      else if (reader.ValueTextEquals("error"u8))
      {
        error = ReadValueRange(ref reader);
        hasError = true;
      }
      else
      {
        reader.Read();
        reader.Skip();
      }
    }

    if (id is { } commandId)
    {
      CompleteCommand(
        commandId,
        hasResult ? message[result] : default,
        hasError ? message[error] : default,
        hasError
      );
    }
    else if (method is not null)
    {
      DispatchEvent(method, sessionId, hasParameters ? message[parameters] : "{}"u8);
    }
  }

  private static Range ReadValueRange(ref Utf8JsonReader reader)
  {
    reader.Read();
    var start = (int)reader.TokenStartIndex;
    reader.Skip();
    return new Range(start, (int)reader.BytesConsumed);
  }

  private void CompleteCommand(
    int id,
    ReadOnlySpan<byte> result,
    ReadOnlySpan<byte> error,
    bool hasError
  )
  {
    // A missing entry means the sender gave up (timeout or cancellation): drop the reply.
    if (!_pending.TryRemove(id, out var pending))
    {
      return;
    }

    if (hasError)
    {
      var (code, text) = ReadError(error);
      pending.TrySetException(new DevToolsProtocolException(pending.Method, code, text));
      return;
    }

    var reply = DevToolsReply.Create(result);
    if (!pending.TrySetResult(reply))
    {
      reply.Dispose();
    }
  }

  private static (int Code, string Message) ReadError(ReadOnlySpan<byte> error)
  {
    var code = 0;
    var text = "Unknown error";
    Utf8JsonReader reader = new(error);
    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
    {
      return (code, text);
    }

    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
    {
      if (reader.ValueTextEquals("code"u8))
      {
        reader.Read();
        code = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : 0;
      }
      else if (reader.ValueTextEquals("message"u8))
      {
        reader.Read();
        text = reader.GetString() ?? text;
      }
      else
      {
        reader.Read();
        reader.Skip();
      }
    }

    return (code, text);
  }

  private void DispatchEvent(string method, string? sessionId, ReadOnlySpan<byte> parameters)
  {
    if (sessionId is not null)
    {
      if (_sessions.TryGetValue(sessionId, out var session))
      {
        InvokeHandler(session.OnEvent, method, parameters);
      }

      return;
    }

    if (method == "Target.detachedFromTarget")
    {
      // The page's session ended without the engine asking: its target crashed or was closed.
      if (ReadStringProperty(parameters, "sessionId"u8) is { } detached)
      {
        TerminateSession(detached, new TargetCrashedException());
      }
    }

    if (EventReceived is { } handler)
    {
      InvokeHandler(handler, method, parameters);
    }
  }

  private void InvokeHandler(
    DevToolsEventHandler handler,
    string method,
    ReadOnlySpan<byte> parameters
  )
  {
    try
    {
      handler(method, parameters);
    }
    catch (Exception exception)
    {
      LogMessages.DevToolsEventHandlerFailed(_logger, exception, method);
    }
  }

  /// <summary>
  /// Reads a top-level string property of a JSON object, or <see langword="null"/>.
  /// </summary>
  internal static string? ReadStringProperty(ReadOnlySpan<byte> json, ReadOnlySpan<byte> name)
  {
    Utf8JsonReader reader = new(json);
    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
    {
      return null;
    }

    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
    {
      var matches = reader.ValueTextEquals(name);
      reader.Read();
      if (matches && reader.TokenType == JsonTokenType.String)
      {
        return reader.GetString();
      }

      reader.Skip();
    }

    return null;
  }

  private void Close(Exception reason)
  {
    if (Interlocked.CompareExchange(ref _closeReason, reason, null) is not null)
    {
      return;
    }

    _closed.TrySetResult();
    foreach (var (id, _) in _pending)
    {
      if (_pending.TryRemove(id, out var pending))
      {
        pending.TrySetException(reason);
      }
    }

    foreach (var (sessionId, _) in _sessions)
    {
      if (_sessions.TryRemove(sessionId, out var session))
      {
        session.Terminate(reason);
      }
    }
  }

  private void ThrowIfClosed()
  {
    if (Volatile.Read(ref _closeReason) is { } reason)
    {
      throw AsClosedException(reason);
    }
  }

  private static BrowserConnectionClosedException AsClosedException(Exception reason) =>
    reason as BrowserConnectionClosedException
    ?? new BrowserConnectionClosedException(reason.Message, reason);

  /// <summary>
  /// Gives up on a command. If its reply raced the timeout or cancellation, the reply's buffer is
  /// returned to the pool once it lands.
  /// </summary>
  private void Abandon(int id, PendingCommand pending)
  {
    if (_pending.TryRemove(id, out _))
    {
      return;
    }

    _ = pending.Task.ContinueWith(
      static task =>
      {
        if (task.IsCompletedSuccessfully)
        {
          task.Result.Dispose();
        }
        else
        {
          _ = task.Exception;
        }
      },
      CancellationToken.None,
      TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default
    );
  }

  /// <summary>
  /// A command waiting for its reply. Continuations run asynchronously so a slow caller never
  /// stalls the receive loop.
  /// </summary>
  private sealed class PendingCommand(string method, string? sessionId)
    : TaskCompletionSource<DevToolsReply>(TaskCreationOptions.RunContinuationsAsynchronously)
  {
    public string Method { get; } = method;

    public string? SessionId { get; } = sessionId;
  }
}
