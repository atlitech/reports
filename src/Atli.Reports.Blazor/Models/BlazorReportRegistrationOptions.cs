using Atli.Reports.Engine;

namespace Atli.Reports.Blazor.Models;

/// <summary>
///  Options for registering a report.
/// </summary>
public class BlazorReportRegistrationOptions
{
  /// <summary>
  /// Output format for the report. Defaults to PDF.
  /// </summary>
  public ReportOutputFormat OutputFormat { get; set; } = ReportOutputFormat.Pdf;

  /// <summary>
  /// The name of the report. This is utilized to generate the route for the report.
  /// </summary>
  public string? ReportName { get; set; }

  /// <summary>
  /// Base styles path for the report.
  /// </summary>
  public string? BaseStylesPath { get; set; }

  /// <summary>
  /// Reads this report's <see cref="BaseStylesPath"/> again for each render. Enable during development
  /// to pick up compiled stylesheet changes without restarting. Defaults to <see langword="false"/>.
  /// </summary>
  public bool BaseStylesReloadOnChange { get; set; }

  /// <summary>
  /// Assets path for the report.
  /// </summary>
  public string? AssetsPath { get; set; }

  /// <summary>
  /// PDF conversion options from Atli.Reports.Engine. Registration starts with a copy of
  /// <see cref="BlazorReportOptions.PdfOptions"/> before applying per-report configuration,
  /// so overrides do not change other reports.
  /// </summary>
  public PdfOptions PdfOptions { get; set; } = new();
}
