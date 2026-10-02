using Atli.Reports.Engine;

namespace Atli.Reports.Blazor.Models;

/// <summary>
/// Default options for Blazor reports.
/// </summary>
public class BlazorReportOptions
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
  /// Default PDF conversion options from Atli.Reports.Engine. Mapped and registered reports start
  /// with their own copy of these options.
  /// </summary>
  public PdfOptions PdfOptions { get; set; } = new();

  /// <summary>
  /// The default settings for reports whose JavaScript must finish before the PDF is printed. Each report
  /// can override them through <see cref="BlazorReportRegistrationOptions.JavaScriptSettings"/>.
  /// </summary>
  public BlazorReportJavaScriptOptions JavaScriptSettings { get; set; } = new();
}
