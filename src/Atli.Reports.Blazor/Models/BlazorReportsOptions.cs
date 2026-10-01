namespace Atli.Reports.Blazor.Models;

/// <summary>
/// Options for BlazorReports
/// </summary>
public class BlazorReportsOptions
{
  /// <summary>
  /// The path to the base styles file to use in this report
  /// </summary>
  public string? BaseStylesPath { get; set; }

  /// <summary>
  /// The path to the assets folder to use in this report
  /// </summary>
  public string? AssetsPath { get; set; }

  /// <summary>
  /// The options for the browser
  /// </summary>
  public BlazorReportsBrowserOptions BrowserOptions { get; set; } = new();

  /// <summary>
  /// Settings for generating a PDF
  /// </summary>
  public BlazorReportsPageSettings PageSettings { get; set; } = new();

  /// <summary>
  /// The default settings for reports whose JavaScript must finish before the PDF is printed. Each report
  /// can override them through <see cref="BlazorReportRegistrationOptions.JavaScriptSettings"/>.
  /// </summary>
  public BlazorReportsJavaScriptSettings JavaScriptSettings { get; set; } = new();
}
