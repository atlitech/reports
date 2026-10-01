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
  /// Assets path for the report.
  /// </summary>
  public string? AssetsPath { get; set; }

  /// <summary>
  /// Settings for generating a PDF
  /// </summary>
  public BlazorReportsPageSettings PageSettings { get; set; } = new();

  /// <summary>
  /// Settings for reports whose JavaScript must finish before the PDF is printed. Reports mapped with
  /// <c>MapBlazorReport</c> start from <see cref="BlazorReportsOptions.JavaScriptSettings"/>.
  /// </summary>
  public BlazorReportsJavaScriptSettings JavaScriptSettings { get; set; } = new();
}
