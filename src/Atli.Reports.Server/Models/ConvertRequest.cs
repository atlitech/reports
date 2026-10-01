namespace Atli.Reports.Server.Models;

public sealed class ConvertRequest
{
  public required string Html { get; set; }
  public PdfOptionsRequest? Options { get; set; }
}

public sealed class PdfOptionsRequest
{
  public string? Orientation { get; set; }
  public string? PaperSize { get; set; }
  public MarginsRequest? Margins { get; set; }
  public bool? PrintBackground { get; set; }
  public double? Scale { get; set; }
  public string? HeaderTemplate { get; set; }
  public string? FooterTemplate { get; set; }
  public bool? DisplayHeaderFooter { get; set; }
  public string? PageRanges { get; set; }
  public bool? PreferCSSPageSize { get; set; }

  /// <summary>
  /// Name of a JS binding to wait for before generating the PDF (e.g., "pdfReady").
  /// The HTML can call <c>window.pdfReady()</c> to signal that rendering is complete.
  /// </summary>
  public string? WaitForSignal { get; set; }

  /// <summary>
  /// Maximum seconds to wait for the signal. Defaults to 30.
  /// </summary>
  public double? WaitTimeoutSeconds { get; set; }
}

public sealed class MarginsRequest
{
  public double? Top { get; set; }
  public double? Bottom { get; set; }
  public double? Left { get; set; }
  public double? Right { get; set; }
}

public sealed class ErrorResponse
{
  public required string Error { get; init; }
}
