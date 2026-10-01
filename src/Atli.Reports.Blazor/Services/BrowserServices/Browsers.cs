namespace Atli.Reports.Blazor.Services.BrowserServices;

/// <summary>
/// The browser to look for when <see cref="Models.BlazorReportsBrowserOptions.BrowserExecutableLocation"/>
/// is not set. Maps onto <see cref="Atli.Reports.Engine.BrowserKind"/>.
/// </summary>
public enum Browsers
{
  /// <summary>
  /// Google Chrome, or Chromium where Chrome is not installed.
  /// </summary>
  Chrome,

  /// <summary>
  /// Microsoft Edge
  /// </summary>
  Edge,
}
