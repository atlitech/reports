namespace Atli.Reports.Blazor.Services.BrowserServices.Helpers;

/// <summary>
/// This class searches for the browser executables cross-platform.
/// </summary>
internal static class BrowserFinder
{
  /// <summary>
  /// Tries to find the browser
  /// </summary>
  /// <param name="browsers"> The browser to find </param>
  /// <returns> The path of the browser executable if found, otherwise null. </returns>
  public static string? Find(Browsers browsers)
  {
    return browsers switch
    {
      Browsers.Chrome => ChromeFinder.Find(),
      Browsers.Edge => EdgeFinder.Find(),
      _ => string.Empty,
    };
  }
}
