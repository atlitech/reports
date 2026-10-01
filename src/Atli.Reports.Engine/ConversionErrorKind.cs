namespace Atli.Reports.Engine;

/// <summary>
/// Categorizes a <see cref="ConversionError"/>.
/// </summary>
public enum ConversionErrorKind
{
  /// <summary>
  /// The browser failed to load the HTML or to print the PDF (including print options the browser
  /// rejects, such as a malformed <see cref="PdfOptions.PageRanges"/>), or the conversion failed for a
  /// reason not covered by a more specific kind.
  /// </summary>
  RenderFailed,

  /// <summary>
  /// The request was rejected before any browser work started: the HTML is empty, or
  /// <see cref="PdfOptions.WaitForSignal"/> is blank, or <see cref="PdfOptions.WaitTimeout"/> is negative.
  /// </summary>
  InvalidRequest,

  /// <summary>
  /// No browser could be used: the executable was not found, the process did not start, or the engine
  /// could not connect to it or open a page in it.
  /// </summary>
  BrowserUnavailable,

  /// <summary>
  /// The engine has no capacity for the conversion right now. Retrying later may succeed.
  /// </summary>
  Busy,

  /// <summary>
  /// The browser did not answer a DevTools command within
  /// <see cref="ReportsEngineBrowserOptions.CommandTimeout"/>.
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
}
