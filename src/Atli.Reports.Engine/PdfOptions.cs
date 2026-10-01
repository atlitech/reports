namespace Atli.Reports.Engine;

/// <summary>
/// Page and rendering options for a single HTML-to-PDF conversion.
/// </summary>
/// <remarks>
/// Lengths are in inches. The defaults produce a portrait US Letter page with 0.4 inch margins and
/// background graphics.
/// </remarks>
public sealed class PdfOptions
{
  /// <summary>
  /// The page orientation. Defaults to <see cref="PageOrientation.Portrait"/>.
  /// </summary>
  public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;

  /// <summary>
  /// The page margins. Defaults to <see cref="Margins.Default"/>.
  /// </summary>
  public Margins Margins { get; set; } = Margins.Default;

  /// <summary>
  /// The paper size. Defaults to <see cref="PaperSize.Letter"/>.
  /// </summary>
  public PaperSize PaperSize { get; set; } = PaperSize.Letter;

  /// <summary>
  /// Whether to print background colors and images. Defaults to <see langword="true"/>.
  /// </summary>
  public bool PrintBackground { get; set; } = true;

  /// <summary>
  /// The rendering scale. Defaults to <c>1.0</c>; Chromium accepts values from <c>0.1</c> to <c>2.0</c>.
  /// </summary>
  public double Scale { get; set; } = 1.0;

  /// <summary>
  /// HTML template for the print header. Used when <see cref="DisplayHeaderFooter"/> is
  /// <see langword="true"/>. Elements with the classes <c>date</c>, <c>title</c>, <c>url</c>,
  /// <c>pageNumber</c>, and <c>totalPages</c> receive the corresponding values.
  /// </summary>
  public string? HeaderTemplate { get; set; }

  /// <summary>
  /// HTML template for the print footer. Uses the same format as <see cref="HeaderTemplate"/>.
  /// </summary>
  public string? FooterTemplate { get; set; }

  /// <summary>
  /// Whether to print the header and footer templates. Defaults to <see langword="false"/>.
  /// </summary>
  public bool DisplayHeaderFooter { get; set; }

  /// <summary>
  /// The pages to print, for example <c>"1-5, 8, 11-13"</c>. <see langword="null"/> prints every page.
  /// </summary>
  public string? PageRanges { get; set; }

  /// <summary>
  /// Whether a CSS <c>@page</c> size declared by the document takes precedence over
  /// <see cref="PaperSize"/>. Defaults to <see langword="false"/>.
  /// </summary>
  public bool PreferCssPageSize { get; set; }

  /// <summary>
  /// Whether the browser writes a tagged (accessible) PDF, with a structure tree that screen readers
  /// and text extraction use. <see langword="null"/> (the default) leaves the choice to the browser;
  /// current Chromium versions tag by default.
  /// </summary>
  /// <remarks>
  /// Tagging costs size and time: for long, table-heavy documents the tagged PDF can be many times
  /// larger and noticeably slower to produce. Set <see langword="false"/> when accessibility is not
  /// needed and size or speed matters; set <see langword="true"/> to require tagging.
  /// </remarks>
  public bool? GenerateTaggedPdf { get; set; }

  /// <summary>
  /// The name of a global JavaScript function the engine exposes to the page, which the page calls to
  /// signal that rendering is complete. <see langword="null"/> (the default) prints once the document
  /// has fired its <c>load</c> event and its fonts are ready.
  /// </summary>
  /// <remarks>
  /// When set to, for example, <c>"pdfReady"</c>, the page calls <c>window.pdfReady()</c> once its
  /// asynchronous work (data fetching, charts, fonts) is done, and the engine prints the PDF at that
  /// moment. If the page does not call it within <see cref="WaitTimeout"/>, the conversion fails with
  /// <see cref="ConversionErrorKind.SignalTimeout"/>. The function exists before any of the page's own
  /// scripts run, and in documents the page navigates to; the HTML itself is not modified, so a
  /// document that starts with <c>&lt;!DOCTYPE html&gt;</c> renders in standards mode.
  /// </remarks>
  public string? WaitForSignal { get; set; }

  /// <summary>
  /// How long to wait for the <see cref="WaitForSignal"/> function to be called. Defaults to 30 seconds.
  /// </summary>
  /// <remarks>
  /// Use <see cref="Timeout.InfiniteTimeSpan"/> to wait until the conversion is canceled. Other negative
  /// values fail the conversion with <see cref="ConversionErrorKind.InvalidRequest"/>.
  /// </remarks>
  public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
