using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Protocol.Messages;

namespace Atli.Reports.Engine.Chromium.Network;

/// <summary>Supplies authorized assets without letting Chromium perform the external fetch.</summary>
internal sealed class PageNetworkController : IAsyncDisposable
{
  private readonly DevToolsSession _session;
  private readonly AssetBroker _broker;
  private readonly DenyNetworkProxy _proxy;
  private readonly CancellationTokenSource _stopping = new();
  private readonly CancellationToken _token;
  private readonly ConcurrentDictionary<long, Task> _pending = new();
  private readonly Lock _gate = new();
  private long _next;
  private bool _disposed;

  public PageNetworkController(
    DevToolsSession session,
    ReportsEngineNetworkOptions options,
    DenyNetworkProxy proxy
  )
  {
    _session = session;
    _broker = new AssetBroker(options);
    _proxy = proxy;
    _token = _stopping.Token;
  }

  public async Task EnableAsync()
  {
    _session.EventReceived += OnEvent;
    _proxy.Rejected += DenyConversion;
    if (_proxy.HasRejected)
    {
      DenyConversion();
    }
    await _session.ExecuteAsync(new DevToolsMessage("Page.enable"), _token);
    await _session.ExecuteAsync(new DevToolsMessage("Network.enable"), _token);
    // No patterns means every request is paused. Do not load content if enabling this fails.
    await _session.ExecuteAsync(new DevToolsMessage("Fetch.enable"), _token);
  }

  private void OnEvent(string method, ReadOnlySpan<byte> parameters)
  {
    if (_stopping.IsCancellationRequested)
    {
      return;
    }

    if (method is "Page.windowOpen" or "Network.webSocketCreated" or "Network.webTransportCreated")
    {
      // Some Chromium versions reject these before contacting the proxy (for example local-
      // network permission checks). The attempt still violates the document rendering contract.
      DenyConversion();
      return;
    }

    if (method != "Fetch.requestPaused")
    {
      return;
    }

    using var document = JsonDocument.Parse(parameters.ToArray());
    var root = document.RootElement;
    var requestId = root.GetProperty("requestId").GetString()!;
    var request = root.GetProperty("request");
    var url = request.GetProperty("url").GetString()!;
    var verb = request.GetProperty("method").GetString()!;
    var resourceType = root.GetProperty("resourceType").GetString();
    var id = Interlocked.Increment(ref _next);
    // The receive loop must remain free to deliver command replies.
    Task task;
    lock (_gate)
    {
      if (_disposed)
      {
        return;
      }

      task = _pending[id] = HandleAsync(requestId, url, verb, resourceType);
    }

    _ = task.ContinueWith(
      _ => _pending.TryRemove(id, out var ignored),
      CancellationToken.None,
      TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default
    );
  }

  private async Task HandleAsync(string requestId, string url, string verb, string? resourceType)
  {
    try
    {
      if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
      {
        if (uri.Scheme is "data" or "blob" || uri.AbsoluteUri == "about:blank")
        {
          DevToolsMessage resume = new("Fetch.continueRequest");
          resume.Parameters.Add("requestId", requestId);
          await _session.ExecuteAsync(resume, _token);
          return;
        }

        // Never turn the service into a URL-to-PDF API or allow external frame navigation.
        if (resourceType != "Document")
        {
          var asset = await _broker.FetchAsync(uri, verb, _token);
          if (asset is not null)
          {
            DevToolsMessage fulfill = new("Fetch.fulfillRequest");
            fulfill.Parameters.Add("requestId", requestId);
            fulfill.Parameters.Add("responseCode", 200);
            fulfill.Parameters.Add("body", Convert.ToBase64String(asset.Body));
            // Only controlled response headers cross the boundary. Never copy Set-Cookie,
            // redirects, authentication challenges, or arbitrary upstream headers.
            fulfill.Parameters.Add(
              "binaryResponseHeaders",
              Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                  $"Content-Type: {asset.ContentType}\0Cache-Control: no-store\0Access-Control-Allow-Origin: *"
                )
              )
            );
            await _session.ExecuteAsync(fulfill, _token);
            return;
          }
        }
      }
    }
    catch (Exception)
    {
      // Network, parser, and broker failures all deny the resource. Do not log secret asset URLs.
      if (_token.IsCancellationRequested || _session.Terminated.IsCompleted)
      {
        return;
      }
    }

    DenyConversion();
  }

  private void DenyConversion() => _session.Fail(new NetworkPolicyException());

  public async ValueTask DisposeAsync()
  {
    _session.EventReceived -= OnEvent;
    _proxy.Rejected -= DenyConversion;
    Task[] pending;
    lock (_gate)
    {
      _disposed = true;
      pending = [.. _pending.Values];
    }

    await _stopping.CancelAsync();
    try
    {
      await Task.WhenAll(pending);
    }
    finally
    {
      _broker.Dispose();
      try
      {
        await _proxy.DisposeAsync();
      }
      finally
      {
        _stopping.Dispose();
      }
    }
  }
}
