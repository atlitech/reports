namespace Atli.Reports.Server.Models;

// The OpenAPI document describes these classes with their XML comments, so the comments are written
// for clients and name the JSON properties (camelCase) rather than refer to C# members.

/// <summary>
/// A document to convert to PDF, and how to print it.
/// </summary>
public sealed class ConvertRequest
{
  /// <summary>
  /// The HTML document. Must not be blank.
  /// </summary>
  public required string Html { get; set; }

  /// <summary>
  /// How to print the document. Omitted, the PDF is a portrait US Letter page with 0.4 inch margins.
  /// </summary>
  public PdfOptionsRequest? Options { get; set; }
}

/// <summary>
/// Page and rendering options. Lengths are in inches. Every option may be omitted.
/// </summary>
public sealed class PdfOptionsRequest
{
  /// <summary>
  /// The page orientation, <c>portrait</c> (the default) or <c>landscape</c>; case-insensitive.
  /// </summary>
  public string? Orientation { get; set; }

  /// <summary>
  /// The paper size, <c>letter</c> (the default), <c>legal</c>, <c>a4</c>, or <c>a3</c>;
  /// case-insensitive. For another size, set <c>paperWidth</c> and <c>paperHeight</c> instead.
  /// </summary>
  public string? PaperSize { get; set; }

  /// <summary>
  /// A custom paper width, in inches; greater than zero. Set together with <c>paperHeight</c>; the
  /// pair overrides <c>paperSize</c>.
  /// </summary>
  public double? PaperWidth { get; set; }

  /// <summary>
  /// A custom paper height, in inches; greater than zero. Set together with <c>paperWidth</c>; the
  /// pair overrides <c>paperSize</c>.
  /// </summary>
  public double? PaperHeight { get; set; }

  /// <summary>
  /// The page margins. Each one defaults to 0.4 inches.
  /// </summary>
  public MarginsRequest? Margins { get; set; }

  /// <summary>
  /// Whether to print background colors and images. Defaults to <c>true</c>.
  /// </summary>
  public bool? PrintBackground { get; set; }

  /// <summary>
  /// The rendering scale, from 0.1 to 2. Defaults to 1.
  /// </summary>
  public double? Scale { get; set; }

  /// <summary>
  /// HTML for the header printed on every page, when <c>displayHeaderFooter</c> is <c>true</c>.
  /// Elements with the classes <c>date</c>, <c>title</c>, <c>url</c>, <c>pageNumber</c>, and
  /// <c>totalPages</c> receive the corresponding values.
  /// </summary>
  public string? HeaderTemplate { get; set; }

  /// <summary>
  /// HTML for the footer printed on every page, in the same format as <c>headerTemplate</c>.
  /// </summary>
  public string? FooterTemplate { get; set; }

  /// <summary>
  /// Whether to print <c>headerTemplate</c> and <c>footerTemplate</c>. Defaults to <c>false</c>.
  /// </summary>
  public bool? DisplayHeaderFooter { get; set; }

  /// <summary>
  /// The pages to print, for example <c>1-5, 8, 11-13</c>. Omitted prints every page.
  /// </summary>
  public string? PageRanges { get; set; }

  /// <summary>
  /// Whether a CSS <c>@page</c> size declared by the document takes precedence over the paper size.
  /// Defaults to <c>false</c>.
  /// </summary>
  public bool? PreferCSSPageSize { get; set; }

  /// <summary>
  /// Whether the browser writes a tagged (accessible) PDF. Defaults to true when omitted or null.
  /// </summary>
  public bool? GenerateTaggedPdf { get; set; }

  /// <summary>
  /// The name of a function the page calls once it has rendered, for example <c>pdfReady</c>: the
  /// page calls <c>window.pdfReady()</c>, and the PDF is printed at that moment. Omitted prints once
  /// the document has loaded.
  /// </summary>
  public string? WaitForSignal { get; set; }

  /// <summary>
  /// How many seconds to wait for the signal, at most 4294967 (about 49 days). Defaults to 30.
  /// <c>-0.001</c> waits until the request is canceled.
  /// </summary>
  public double? WaitTimeoutSeconds { get; set; }
}

/// <summary>
/// Page margins, in inches.
/// </summary>
public sealed class MarginsRequest
{
  /// <summary>The top margin. Defaults to 0.4.</summary>
  public double? Top { get; set; }

  /// <summary>The bottom margin. Defaults to 0.4.</summary>
  public double? Bottom { get; set; }

  /// <summary>The left margin. Defaults to 0.4.</summary>
  public double? Left { get; set; }

  /// <summary>The right margin. Defaults to 0.4.</summary>
  public double? Right { get; set; }
}
