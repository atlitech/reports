namespace Atli.Reports.Engine.Chromium.Page;

/// <summary>
/// Factory for creating Chromium pages
/// </summary>
internal interface IChromiumPageFactory
{
  /// <summary>
  /// Creates a new browser page
  /// </summary>
  /// <param name="targetId">The target id</param>
  /// <param name="pageUri">The page uri</param>
  /// <returns>The browser page</returns>
  ValueTask<ChromiumPage> CreatePage(string targetId, Uri pageUri);
}
