using Atli.Reports.Engine;

namespace Atli.Reports.Blazor.Models;

/// <summary>
/// Represents a Blazor report.
/// </summary>
public class BlazorReport
{
  /// <summary>
  /// Output format for the report. Defaults to PDF.
  /// </summary>
  public ReportOutputFormat OutputFormat { get; set; } = ReportOutputFormat.Pdf;

  /// <summary>
  /// The name of the report.
  /// </summary>
  public required string Name { get; set; }

  /// <summary>
  /// The normalized name of the report.
  /// </summary>
  public required string NormalizedName { get; set; }

  /// <summary>
  /// The type of the component to render.
  /// </summary>
  public required Type Component { get; set; }

  /// <summary>
  /// Base styles path to use for the report.
  /// </summary>
  public string? BaseStyles { get; set; }

  /// <summary>
  /// Assets path to use for the report.
  /// </summary>
  public Dictionary<string, string> Assets { get; set; } = [];

  /// <summary>
  /// The resolved PDF options for this report, copied from the defaults at registration time.
  /// </summary>
  public PdfOptions PdfOptions { get; set; } = new();
}
