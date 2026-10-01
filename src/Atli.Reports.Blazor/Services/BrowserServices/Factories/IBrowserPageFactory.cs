namespace Atli.Reports.Blazor.Services.BrowserServices.Factories;

internal interface IBrowserPageFactory
{
  /// <summary>
  /// Creates a new browser page
  /// </summary>
  /// <param name="targetId"> The target id </param>
  /// <param name="pageUri"> The page uri </param>
  /// <returns> The browser page </returns>
  ValueTask<BrowserPage> CreateBrowserPage(string targetId, Uri pageUri);
}
