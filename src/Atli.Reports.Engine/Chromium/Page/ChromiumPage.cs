using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Protocol.Messages;
using Atli.Reports.Engine.Chromium.Protocol.Responses;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Engine.Chromium.Page;

/// <summary>
/// A page (tab) in a Chromium browser, driven over its own DevTools connection.
/// </summary>
internal sealed class ChromiumPage : IAsyncDisposable
{
  private readonly ILogger<ChromiumPage> _logger;
  private readonly string _targetId;
  private readonly DevToolsConnection _connection;
  private int _usageCount;
  private bool _hasError;

  /// <summary>
  /// Unique identifier for this page
  /// </summary>
  public string PageId => _targetId;

  /// <summary>
  /// Number of times this page has been used
  /// </summary>
  public int UsageCount => _usageCount;

  /// <summary>
  /// Whether this page has encountered an error
  /// </summary>
  public bool HasError => _hasError;

  /// <summary>
  /// When this page was created
  /// </summary>
  public DateTimeOffset CreatedAt { get; }

  /// <summary>
  /// Age of the page since creation
  /// </summary>
  public TimeSpan Age => DateTimeOffset.UtcNow - CreatedAt;

  /// <summary>
  /// Gets the underlying DevTools connection
  /// </summary>
  internal DevToolsConnection Connection => _connection;

  internal ChromiumPage(
    ILogger<ChromiumPage> logger,
    string targetId,
    DevToolsConnection connection
  )
  {
    _logger = logger;
    _targetId = targetId;
    _connection = connection;
    CreatedAt = DateTimeOffset.UtcNow;
  }

  /// <summary>
  /// Increments the usage count for this page
  /// </summary>
  internal void IncrementUsage()
  {
    Interlocked.Increment(ref _usageCount);
  }

  /// <summary>
  /// Marks this page as having encountered an error
  /// </summary>
  public void MarkAsErrored()
  {
    _hasError = true;
  }

  /// <summary>
  /// Resets the page state for reuse
  /// </summary>
  internal void ResetState()
  {
    _hasError = false;
  }

  /// <summary>
  /// Sets HTML content directly
  /// </summary>
  public async ValueTask<OneOf<Success, RenderError>> SetContentAsync(
    string html,
    CancellationToken ct = default
  )
  {
    try
    {
      IncrementUsage();
      await _connection.ConnectAsync(ct);

      if (string.IsNullOrWhiteSpace(html))
      {
        MarkAsErrored();
        return new RenderError("HTML content cannot be null or whitespace");
      }

      // Enable/disable cache
      DevToolsMessage cacheMessage = new("Network.setCacheDisabled");
      cacheMessage.Parameters.Add("cacheDisabled", false);
      _connection.SendAsync(cacheMessage);

      // Get frame tree and set document content
      DevToolsMessage getFrameTreeMessage = new("Page.getFrameTree");
      await _connection.SendAsync(
        getFrameTreeMessage,
        GetFrameTreeResponseSerializationContext.Default.DevToolsResponseGetFrameTreeResponse,
        response =>
        {
          if (response.Result?.FrameTree?.Frame?.Id == null)
          {
            throw new InvalidOperationException("Frame ID not found in response");
          }

          DevToolsMessage setContentMessage = new("Page.setDocumentContent");
          setContentMessage.Parameters.Add("frameId", response.Result.FrameTree.Frame.Id);
          setContentMessage.Parameters.Add("html", html);
          _connection.SendAsync(setContentMessage);
        },
        ct
      );

      return new Success();
    }
    catch (Exception ex)
    {
      MarkAsErrored();
      LogMessages.BrowserPageSetContentFailed(_logger, ex, _targetId);
      return new RenderError("Failed to set HTML content", ex);
    }
  }

  /// <summary>
  /// Registers a JavaScript binding that acts as a signal for rendering completion.
  /// The binding is confirmed active before this method returns, so it is safe to load
  /// HTML content immediately after — JavaScript can call <c>window.bindingName()</c>
  /// and the returned <see cref="SignalAwaiter"/> will complete.
  /// </summary>
  /// <param name="bindingName">Name of the global function exposed to JavaScript</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>A <see cref="SignalAwaiter"/> that completes when JS calls the binding</returns>
  public async ValueTask<SignalAwaiter> RegisterSignalAsync(
    string bindingName,
    CancellationToken ct = default
  )
  {
    await _connection.ConnectAsync(ct);

    // Enable the Runtime domain (idempotent — no-op if already enabled)
    DevToolsMessage enableRuntime = new("Runtime.enable");
    await _connection.SendAsync(
      enableRuntime,
      EmptyResponseSerializationContext.Default.DevToolsResponseEmptyResponse,
      ct
    );

    // Register the binding — this must complete before HTML is loaded.
    // Note: Runtime.addBinding creates a function that requires exactly one string
    // argument. The caller must prepend a wrapper <script> to the HTML so that
    // user code can call window.signalName() without arguments.
    DevToolsMessage addBinding = new("Runtime.addBinding");
    addBinding.Parameters.Add("name", bindingName);
    await _connection.SendAsync(
      addBinding,
      EmptyResponseSerializationContext.Default.DevToolsResponseEmptyResponse,
      ct
    );

    LogMessages.SignalBindingRegistered(_logger, _targetId, bindingName);
    return new SignalAwaiter(bindingName, _connection, _logger);
  }

  /// <summary>
  /// Disposes the page and its connection
  /// </summary>
  public async ValueTask DisposeAsync()
  {
    LogMessages.BrowserPageDispose(_logger, _targetId);
    await _connection.DisposeAsync();
  }
}
