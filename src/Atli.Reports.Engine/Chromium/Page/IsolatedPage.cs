using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Protocol;
using Atli.Reports.Engine.Chromium.Protocol.Messages;
using Atli.Reports.Engine.Chromium.Protocol.Results;
using Atli.Reports.Engine.Conversion;
using Atli.Reports.Engine.Diagnostics;
using Atli.Reports.Engine.Pdf;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Chromium.Page;

/// <summary>
/// A page in a browser context of its own, created for one conversion and discarded with its
/// context afterwards.
/// </summary>
/// <remarks>
/// A browser context is Chromium's isolation boundary: it has its own cookie jar, storage (local,
/// session, IndexedDB, Cache Storage), HTTP cache, service workers, and permissions. Disposing the
/// context discards all of it, so nothing a document stores can reach another conversion.
/// </remarks>
internal sealed class IsolatedPage : IConversionPage
{
  /// <summary>
  /// Resolves once the document has loaded and its fonts are ready.
  /// </summary>
  private const string LoadedScript = """
    new Promise(function (resolve) {
      if (document.readyState === 'complete') { resolve(); return; }
      window.addEventListener('load', function () { resolve(); }, { once: true });
    }).then(function () {
      return document.fonts ? document.fonts.ready : null;
    }).then(function () { return true; })
    """;

  private const int MaxLoadWaitAttempts = 3;

  private readonly BrowserInstance _browser;
  private readonly DevToolsSession _session;
  private readonly string _browserContextId;
  private readonly int _pdfReadChunkSize;
  private readonly ILogger _logger;
  private TaskCompletionSource? _signal;
  private int _disposed;

  internal IsolatedPage(
    BrowserInstance browser,
    DevToolsSession session,
    string browserContextId,
    int pdfReadChunkSize,
    ILogger logger
  )
  {
    _browser = browser;
    _session = session;
    _browserContextId = browserContextId;
    _pdfReadChunkSize = pdfReadChunkSize;
    _logger = logger;
  }

  public async Task EnableSignalAsync(string signalName, CancellationToken cancellationToken)
  {
    _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _session.EventReceived += OnEvent;

    // The Page and Runtime domains must be enabled for the script and the binding to reach the
    // documents a page navigates to, not just the one already loaded.
    DevToolsMessage addBinding = new("Runtime.addBinding");
    addBinding.Parameters.Add("name", SignalShim.BindingName);

    // runImmediately also defines the signal in the blank document that is already loaded;
    // Page.setDocumentContent keeps that window, so the page's own scripts see it.
    DevToolsMessage addScript = new("Page.addScriptToEvaluateOnNewDocument");
    addScript.Parameters.Add("source", SignalShim.CreateScript(signalName));
    addScript.Parameters.Add("runImmediately", true);

    // The commands run in order, so they are sent together and awaited together.
    await Task.WhenAll(
      _session.ExecuteAsync(new DevToolsMessage("Page.enable"), cancellationToken),
      _session.ExecuteAsync(new DevToolsMessage("Runtime.enable"), cancellationToken),
      _session.ExecuteAsync(addBinding, cancellationToken),
      _session.ExecuteAsync(addScript, cancellationToken)
    );
  }

  public Task SetContentAsync(string html, CancellationToken cancellationToken)
  {
    DevToolsMessage setContent = new("Page.setDocumentContent");
    setContent.Parameters.Add("frameId", _session.TargetId);
    setContent.Parameters.Add("html", html);
    return _session.ExecuteAsync(setContent, cancellationToken);
  }

  public async Task<bool> WaitForSignalAsync(TimeSpan timeout, CancellationToken cancellationToken)
  {
    var signal = _signal ?? throw new InvalidOperationException("The signal has not been enabled.");

    try
    {
      await Task.WhenAny(signal.Task, _session.Terminated).WaitAsync(timeout, cancellationToken);
    }
    catch (TimeoutException)
    {
      return false;
    }

    if (!signal.Task.IsCompleted)
    {
      // The page crashed or the browser went away while the conversion waited.
      await _session.Terminated;
    }

    return true;
  }

  public async Task WaitForLoadAsync(CancellationToken cancellationToken)
  {
    DevToolsMessage evaluate = new("Runtime.evaluate");
    evaluate.Parameters.Add("expression", LoadedScript);
    evaluate.Parameters.Add("awaitPromise", true);
    evaluate.Parameters.Add("returnByValue", true);

    for (var attempt = 1; ; attempt++)
    {
      try
      {
        using var reply = await _session.SendAsync(evaluate, cancellationToken);
        var result = reply.Deserialize(DevToolsResultsContext.Default.EvaluateResult);
        if (result.ExceptionDetails is { } exception)
        {
          // The page broke the globals the script relies on. Print what is there.
          LogMessages.LoadWaitFailed(
            _logger,
            exception.Exception?.Description ?? exception.Text ?? "unknown error"
          );
        }

        return;
      }
      catch (DevToolsProtocolException) when (attempt < MaxLoadWaitAttempts)
      {
        // The document navigated while the script waited, which destroys the script's context.
        // Wait for the new document instead.
      }
    }
  }

  public async Task<long> PrintToPdfAsync(
    PdfOptions options,
    Stream destination,
    CancellationToken cancellationToken
  )
  {
    string handle;
    using (
      var printed = await _session.SendAsync(
        ChromiumPdfGenerator.CreatePrintToPdfMessage(options),
        cancellationToken
      )
    )
    {
      handle =
        printed.Deserialize(DevToolsResultsContext.Default.PrintToPdfResult).Stream
        ?? throw new DevToolsProtocolException("Page.printToPDF returned no PDF stream.");
    }

    // The browser has printed; what remains is moving the PDF out of it.
    using var activity = EngineActivities.Source.StartActivity(EngineActivities.Spans.Stream);
    try
    {
      var size = await ChromiumPdfGenerator.CopyToAsync(
        _session,
        handle,
        destination,
        _pdfReadChunkSize,
        cancellationToken
      );
      activity?.SetTag(EngineActivities.Tags.PdfSize, size);
      return size;
    }
    catch (Exception exception) when (activity is not null)
    {
      EngineActivities.StageFailed(
        activity,
        exception is DestinationWriteException { InnerException: { } inner } ? inner : exception
      );
      throw;
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref _disposed, 1) == 1)
    {
      return;
    }

    _session.EventReceived -= OnEvent;
    _session.Dispose();
    await _browser.DisposeContextAsync(_browserContextId);
  }

  private void OnEvent(string method, ReadOnlySpan<byte> parameters)
  {
    if (
      method == "Runtime.bindingCalled"
      && DevToolsConnection.ReadStringProperty(parameters, "name"u8) == SignalShim.BindingName
    )
    {
      _signal?.TrySetResult();
    }
  }
}
