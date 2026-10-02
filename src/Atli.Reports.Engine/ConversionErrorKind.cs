namespace Atli.Reports.Engine;

/// <summary>
/// Categorizes a <see cref="ConversionError"/>.
/// </summary>
public enum ConversionErrorKind
{
  /// <summary>
  /// The browser failed to load the HTML or to print the PDF (including print options the browser
  /// rejects, such as a malformed <see cref="PdfOptions.PageRanges"/>), the page crashed while it
  /// rendered (for example because its renderer ran out of memory), or the conversion failed for a
  /// reason not covered by a more specific kind.
  /// </summary>
  RenderFailed,

  /// <summary>
  /// The request was rejected before any browser work started: the HTML is empty, or
  /// <see cref="PdfOptions.WaitForSignal"/> is blank, or <see cref="PdfOptions.WaitTimeout"/> is negative.
  /// </summary>
  InvalidRequest,

  /// <summary>
  /// No browser could be used: the executable was not found, the process did not start, the engine
  /// could not connect to it or open a page in it, the browser exited or disconnected while the
  /// conversion ran, or the engine is shutting down. The engine starts a new browser for the next
  /// conversion, so retrying usually succeeds unless the browser cannot start at all.
  /// </summary>
  BrowserUnavailable,

  /// <summary>
  /// The engine has no capacity for the conversion right now: the queue of conversions waiting for a
  /// turn was full, or the conversion waited longer than
  /// <see cref="ReportsEngineConcurrencyOptions.QueueTimeout"/>. Retrying later may succeed.
  /// </summary>
  Busy,

  /// <summary>
  /// The browser did not answer a DevTools command within
  /// <see cref="ReportsEngineBrowserOptions.CommandTimeout"/> (which also bounds the wait for the
  /// document's <c>load</c> event), or the conversion as a whole ran past
  /// <see cref="ReportsEngineOptions.ConversionTimeout"/>.
  /// </summary>
  Timeout,

  /// <summary>
  /// The page did not call the function named by <see cref="PdfOptions.WaitForSignal"/> within
  /// <see cref="PdfOptions.WaitTimeout"/>.
  /// </summary>
  SignalTimeout,

  /// <summary>
  /// The caller canceled the conversion through its <see cref="CancellationToken"/>.
  /// </summary>
  Canceled,

  /// <summary>
  /// The reports server requires a valid caller credential. Acquire or configure credentials
  /// before trying again; repeating the same request does not authenticate it.
  /// </summary>
  Unauthorized,

  /// <summary>
  /// The authenticated caller is not permitted to request the conversion.
  /// </summary>
  Forbidden,

  /// <summary>
  /// The document attempted to access a resource forbidden by the rendering policy.
  /// Change the document or the authorized rendering policy before trying again.
  /// </summary>
  PolicyDenied,
}
