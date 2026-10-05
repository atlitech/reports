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
  /// Reads <see cref="BaseStylesPath"/> again for each render. Enable during development to pick up
  /// compiled stylesheet changes without restarting. Defaults to <see langword="false"/>.
  /// </summary>
  public bool BaseStylesReloadOnChange { get; set; }

  /// <summary>
  /// The path to the assets folder to use in this report
  /// </summary>
  public string? AssetsPath { get; set; }

  /// <summary>
  /// Default PDF conversion options from Atli.Reports.Engine. Mapped and registered reports start
  /// with their own copy of these options.
  /// </summary>
  public PdfOptions PdfOptions { get; set; } = new();
}
