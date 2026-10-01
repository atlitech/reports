using Atli.Reports.Blazor.Services.BrowserServices.Problems;
using OneOf;

namespace Atli.Reports.Blazor.Services.BrowserServices.Factories;

/// <summary>
/// Factory for creating browser instances
/// </summary>
internal interface IBrowserFactory
{
  /// <summary>
  /// Creates a new browser instance
  /// </summary>
  /// <returns> The browser instance </returns>
  ValueTask<OneOf<Browser, BrowserProblem>> CreateBrowser();
}
