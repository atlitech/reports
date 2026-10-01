using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Protocol.Messages;
using Atli.Reports.Engine.Chromium.Protocol.Responses;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Engine.Chromium.Page;

/// <summary>
/// Awaits a JavaScript signal via a registered CDP binding.
/// Created by <see cref="ChromiumPage.RegisterSignalAsync"/> — the binding is already active
/// when this object is returned, so it is safe to load HTML content before calling <see cref="WaitAsync"/>.
/// </summary>
internal sealed class SignalAwaiter : IAsyncDisposable
{
  private readonly string _bindingName;
  private readonly DevToolsConnection _connection;
  private readonly ILogger _logger;
  private readonly TaskCompletionSource _signalTcs = new(
    TaskCreationOptions.RunContinuationsAsynchronously
  );
  private readonly EventHandler<DevToolsEventArgs> _handler;
  private bool _disposed;

  internal SignalAwaiter(string bindingName, DevToolsConnection connection, ILogger logger)
  {
    _bindingName = bindingName;
    _connection = connection;
    _logger = logger;

    _handler = OnEventReceived;
    _connection.EventReceived += _handler;
  }

  private void OnEventReceived(object? sender, DevToolsEventArgs e)
  {
    if (e.Method != "Runtime.bindingCalled")
    {
      return;
    }

    if (
      e.Parameters.TryGetProperty("name", out var nameElement)
      && nameElement.GetString() == _bindingName
    )
    {
      LogMessages.SignalReceived(_logger, _bindingName);
      _signalTcs.TrySetResult();
    }
  }

  /// <summary>
  /// Waits for the JavaScript signal to fire, or times out
  /// </summary>
  public async ValueTask<OneOf<Success, TimeoutError>> WaitAsync(
    TimeSpan timeout,
    CancellationToken ct = default
  )
  {
    LogMessages.WaitingForSignal(_logger, _bindingName, timeout);

    using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(timeout);

    try
    {
      await _signalTcs.Task.WaitAsync(timeoutCts.Token);
      return new Success();
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      LogMessages.SignalTimedOut(_logger, _bindingName, timeout);
      return new TimeoutError($"Signal '{_bindingName}'", timeout);
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _connection.EventReceived -= _handler;

    // Remove the binding so it doesn't leak into subsequent page uses
    try
    {
      DevToolsMessage removeBinding = new("Runtime.removeBinding");
      removeBinding.Parameters.Add("name", _bindingName);
      await _connection.SendAsync(
        removeBinding,
        EmptyResponseSerializationContext.Default.DevToolsResponseEmptyResponse
      );
    }
    catch
    {
      // Best-effort cleanup — connection may already be closed
    }
  }
}
