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
  /// The type of the data to pass to the component.
  /// </summary>
  public required Type? Data { get; set; }

  /// <summary>
  /// Base styles path to use for the report.
  /// </summary>
  public string? BaseStyles { get; set; }

  /// <summary>
  /// Assets path to use for the report.
  /// </summary>
  public Dictionary<string, string> Assets { get; set; } = [];

  /// <summary>
  /// The page settings to use for the report.
  /// </summary>
  public BlazorReportsPageSettings? PageSettings { get; set; }

  /// <summary>
  /// The JavaScript settings to use for the report. <see langword="null"/> uses
  /// <see cref="BlazorReportRegistry.DefaultJavaScriptSettings"/>.
  /// </summary>
  public BlazorReportsJavaScriptSettings? JavaScriptSettings { get; set; }
}
