using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Atli.Reports.Blazor.Services.BrowserServices.Logs;
using Atli.Reports.Blazor.Services.BrowserServices.Problems;
using Atli.Reports.Blazor.Services.BrowserServices.Requests;
using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Services.BrowserServices;

/// <summary>
/// Represents a connection to the browser
/// </summary>
/// <remarks>
/// The constructor of the connection
/// </remarks>
/// <param name="uri"> The uri of the connection</param>
/// <param name="responseTimeout"> The response timeout</param>
/// <param name="logger"> The logger</param>
internal sealed class Connection(Uri uri, TimeSpan responseTimeout, ILogger<Connection> logger)
  : IAsyncDisposable
{
  private ClientWebSocket _webSocket = new();
  private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
  private readonly SemaphoreSlim _sendSignal = new(0);
  private readonly ConcurrentQueue<BrowserMessage> _sendQueue = new();
  private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _responseTasks =
    new();
  private const int BufferSize = 100 * 1024;
  private int _lastMessageId;
  private Task? _sendTask;
  private Task? _receiveTask;
  private readonly SemaphoreSlim _connectionLock = new(1, 1);
  private readonly CancellationTokenSource _cts = new();

  /// <summary>
  /// The uri of the connection
  /// </summary>
  public readonly Uri Uri = uri;

  public async Task InitializeAsync(CancellationToken stoppingToken = default)
  {
    await _connectionLock.WaitAsync(stoppingToken);
    try
    {
      if (_webSocket.State is not WebSocketState.None)
      {
        return;
      }

      await _webSocket.ConnectAsync(Uri, stoppingToken);

      _sendTask = ProcessSendQueueAsync();
      _receiveTask = ProcessResponsesAsync();
    }
    finally
    {
      _connectionLock.Release();
    }
  }

  /// <summary>
  /// Connects to the browser
  /// </summary>
  /// <param name="stoppingToken"> Token to stop the task</param>
  /// <returns> A task that represents the asynchronous operation</returns>
  /// <exception cref="Exception"> Thrown when the connection could not be established</exception>
  public async ValueTask<OneOf<Success, ConnectionProblem>> ConnectAsync(
    CancellationToken stoppingToken = default
  )
  {
    if (_webSocket.State is WebSocketState.Open)
    {
      return new Success();
    }

    await _connectionLock.WaitAsync(stoppingToken);

    var retries = 3;
    try
    {
      while (retries > 0)
      {
        try
        {
          await _webSocket.ConnectAsync(Uri, stoppingToken);
          retries = 0; // connection successful, no more retries needed
        }
        catch (Exception)
        {
          _webSocket.Dispose();
          _webSocket = new ClientWebSocket();

          retries--; // decrease remaining retries

          if (retries > 0) // don't delay if no more retries left
          {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); // wait for 3 seconds before next attempt
          }
        }
      }

      if (_webSocket.State is not WebSocketState.Open)
      {
        LogMessages.UnableToEstablishWebSocketConnection(logger, Uri);
        return new ConnectionProblem();
      }

      return new Success();
    }
    finally
    {
      _connectionLock.Release();
    }
  }

  private async Task ProcessSendQueueAsync()
  {
    byte[]? bufferToSend = null;
    try
    {
      while (!_cts.IsCancellationRequested)
      {
        await _sendSignal.WaitAsync(_cts.Token);

        if (!_sendQueue.TryDequeue(out var message))
        {
          continue;
        }

        var buffer = JsonSerializer.SerializeToUtf8Bytes(
          message,
          BrowserMessageSerializationContext.Default.BrowserMessage
        );
        bufferToSend = _bufferPool.Rent(buffer.Length);
        Memory<byte> bufferToSendMemory = new(bufferToSend);
        buffer.CopyTo(bufferToSendMemory);
        await _webSocket.SendAsync(
          bufferToSendMemory[..buffer.Length],
          WebSocketMessageType.Text,
          true,
          _cts.Token
        );
      }
    }
    catch (OperationCanceledException)
    {
      LogMessages.SendQueueProcessingCancelled(logger, Uri);
    }
    finally
    {
      if (bufferToSend is not null)
      {
        _bufferPool.Return(bufferToSend);
      }
    }
  }

  private async Task ProcessResponsesAsync()
  {
    var bufferToReceive = _bufferPool.Rent(BufferSize);
    Memory<byte> bufferToReceiveMemory = new(bufferToReceive);

    try
    {
      while (!_cts.IsCancellationRequested)
      {
        var result = await _webSocket.ReceiveAsync(bufferToReceiveMemory, _cts.Token);

        var messageReceived = bufferToReceiveMemory[..result.Count];
        JsonDocument jsonDoc = JsonDocument.Parse(messageReceived);
        var root = jsonDoc.RootElement;
        if (!root.TryGetProperty("id", out var methodElement))
        {
          continue;
        }

        var id = methodElement.GetInt32();

        if (_responseTasks.TryRemove(id, out var taskSource))
        {
          taskSource.SetResult(jsonDoc);
        }
      }
    }
    catch (OperationCanceledException)
    {
      LogMessages.ReceiveQueueProcessingCancelled(logger, Uri);
    }
    catch (WebSocketException)
    {
      // The remote endpoint closed the WebSocket connection without completing the close handshake
    }
    catch (Exception ex)
    {
      LogMessages.ReceiveQueueProcessingError(logger, ex, Uri);
    }
    finally
    {
      _bufferPool.Return(bufferToReceive);
    }
  }

  private async Task<JsonDocument> SendMessageAsync(
    BrowserMessage message,
    CancellationToken stoppingToken
  )
  {
    message.Id = Interlocked.Increment(ref _lastMessageId);
    _sendQueue.Enqueue(message);
    _sendSignal.Release();

    TaskCompletionSource<JsonDocument> tcs = new();
    _responseTasks[message.Id] = tcs;

    using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
      stoppingToken
    );
    timeoutCts.CancelAfter(responseTimeout);

    if (await Task.WhenAny(tcs.Task, Task.Delay(-1, timeoutCts.Token)) == tcs.Task)
    {
      return await tcs.Task;
    }
    else
    {
      _responseTasks.TryRemove(message.Id, out _);
      throw new TimeoutException("The operation timed out.");
    }
  }

  public async ValueTask<T> SendAsync<T>(
    BrowserMessage message,
    JsonTypeInfo<T> returnDataJsonTypeInfo,
    CancellationToken stoppingToken = default
  )
  {
    var response = await SendMessageAsync(message, stoppingToken);
    var parsedMessage = response.RootElement.Deserialize(returnDataJsonTypeInfo);

    return parsedMessage is null
      ? throw new JsonException("Could not deserialize response")
      : parsedMessage;
  }

  /// <summary>
  /// Sends a message to the browser
  /// </summary>
  /// <param name="message"> The message to send</param>
  /// <param name="returnDataJsonTypeInfo"> The json type info of the return data</param>
  /// <param name="responseHandler"> The response handler</param>
  /// <param name="stoppingToken"> Token to stop the task</param>
  public async ValueTask<TR> SendAsync<T, TR>(
    BrowserMessage message,
    JsonTypeInfo<T> returnDataJsonTypeInfo,
    Func<T, Task<TR>> responseHandler,
    CancellationToken stoppingToken = default
  )
  {
    var response = await SendMessageAsync(message, stoppingToken);
    var parsedMessage = response.RootElement.Deserialize(returnDataJsonTypeInfo);

    return parsedMessage is null
      ? throw new JsonException("Could not deserialize response")
      : await responseHandler(parsedMessage);
  }

  /// <summary>
  /// Sends a message to the browser
  /// </summary>
  /// <param name="message"> The message to send</param>
  /// <param name="returnDataJsonTypeInfo"> The json type info of the return data</param>
  /// <param name="responseAction"> The response action</param>
  /// <param name="stoppingToken"> Token to stop the task</param>
  public async ValueTask SendAsync<T>(
    BrowserMessage message,
    JsonTypeInfo<T> returnDataJsonTypeInfo,
    Action<T> responseAction,
    CancellationToken stoppingToken = default
  )
  {
    var response = await SendMessageAsync(message, stoppingToken);
    var parsedMessage =
      response.RootElement.Deserialize(returnDataJsonTypeInfo)
      ?? throw new JsonException("Could not deserialize response");
    responseAction(parsedMessage);
  }

  public void SendAsync(BrowserMessage message)
  {
    message.Id = Interlocked.Increment(ref _lastMessageId);
    _sendQueue.Enqueue(message);
    _sendSignal.Release();
  }

  public async ValueTask SendAsync<T>(
    BrowserMessage message,
    JsonTypeInfo<T> returnDataJsonTypeInfo,
    Func<T, Task> responseAction,
    CancellationToken stoppingToken = default
  )
  {
    message.Id = Interlocked.Increment(ref _lastMessageId);
    _sendQueue.Enqueue(message);
    _sendSignal.Release();

    TaskCompletionSource<JsonDocument> tcs = new();
    _responseTasks[message.Id] = tcs;

    if (await Task.WhenAny(tcs.Task, Task.Delay(responseTimeout, stoppingToken)) == tcs.Task)
    {
      var response = await tcs.Task;
      var parsedMessage =
        response.RootElement.Deserialize(returnDataJsonTypeInfo)
        ?? throw new JsonException("Could not deserialize response");
      await responseAction(parsedMessage);
    }
    else
    {
      _responseTasks.TryRemove(message.Id, out _);
      throw new TimeoutException("The operation timed out.");
    }
  }

  /// <summary>
  /// Closes the connection
  /// </summary>
  /// <param name="stoppingToken"></param>
  public async ValueTask CloseAsync(CancellationToken stoppingToken = default)
  {
    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", stoppingToken);
  }

  public async ValueTask DisposeAsync()
  {
    await _cts.CancelAsync();

    if (_sendTask is not null)
    {
      await _sendTask;
    }

    if (_receiveTask is not null)
    {
      await _receiveTask;
    }

    _webSocket.Dispose();
    _sendSignal.Dispose();
    _connectionLock.Dispose();
    _responseTasks.Clear();
  }
}
