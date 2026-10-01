namespace Atli.Reports.Engine.Chromium.Discovery;

/// <summary>
/// This class searches for the browser executables cross-platform.
/// </summary>
internal static class BrowserFinder
{
  /// <summary>
  /// Tries to find the browser
  /// </summary>
  /// <param name="browserKind">The browser to find</param>
  /// <returns>The path of the browser executable if found, otherwise null.</returns>
  public static string? Find(BrowserKind browserKind)
  {
    return browserKind switch
    {
      BrowserKind.Chrome => ChromeFinder.Find(),
      BrowserKind.Edge => EdgeFinder.Find(),
      _ => null,
    };
  }
}
