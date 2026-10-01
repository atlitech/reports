namespace Atli.Reports.Engine;

/// <summary>
/// The Chromium-based browser the engine looks for when no executable path is configured.
/// </summary>
public enum BrowserKind
{
  /// <summary>
  /// Google Chrome, or Chromium where Chrome is not installed.
  /// </summary>
  Chrome,

  /// <summary>
  /// Microsoft Edge.
  /// </summary>
  Edge,
}
