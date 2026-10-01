using OneOf;

namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// Factory for creating browser instances
/// </summary>
internal interface IBrowserFactory
{
  /// <summary>
  /// Creates a new browser instance using configured options
  /// </summary>
  /// <param name="ct">Cancellation token</param>
  /// <returns>A browser instance or an error</returns>
  ValueTask<OneOf<IBrowser, BrowserError>> CreateBrowserAsync(CancellationToken ct = default);
}
