using Atli.Reports.Blazor.Enums;

namespace Atli.Reports.Blazor.Models;

/// <summary>
/// Settings for generating a PDF
/// </summary>
public class BlazorReportsPageSettings
{
  /// <summary>
  /// Orientation of the PDF. Defaults to portrait.
  /// </summary>
  public BlazorReportsPageOrientation Orientation { get; set; } =
    BlazorReportsPageOrientation.Portrait;

  /// <summary>
  /// Top margin in inches. Defaults to 0.4 inches.
  /// </summary>
  public double MarginTop { get; set; } = 0.4;

  /// <summary>
  /// Bottom margin in inches. Defaults to 0.4 inches.
  /// </summary>
  public double MarginBottom { get; set; } = 0.4;

  /// <summary>
  /// Left margin in inches. Defaults to 0.4 inches.
  /// </summary>
  public double MarginLeft { get; set; } = 0.4;

  /// <summary>
  /// Right margin in inches. Defaults to 0.4 inches.
  /// </summary>
  public double MarginRight { get; set; } = 0.4;

  /// <summary>
  /// Paper width in inches. Defaults to 11 inches.
  /// </summary>
  public double PaperHeight { get; set; } = 11;

  /// <summary>
  /// Paper height in inches. Defaults to 8.5 inches.
  /// </summary>
  public double PaperWidth { get; set; } = 8.5;

  /// <summary>
  /// Whether to ignore the background of the page. Defaults to false.
  /// </summary>
  public bool IgnoreBackground { get; set; }
}
