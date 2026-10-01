using Atli.Reports.Engine.Chromium.Page;
using OneOf;

namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// Represents a browser instance
/// </summary>
internal interface IBrowser : IAsyncDisposable
{
  /// <summary>
  /// Creates or retrieves a page from the pool
  /// </summary>
  /// <param name="ct">Cancellation token</param>
  /// <returns>A browser page or an error</returns>
  ValueTask<OneOf<ChromiumPage, BrowserError>> CreatePageAsync(CancellationToken ct = default);

  /// <summary>
  /// Returns a page to the pool for reuse
  /// </summary>
  /// <param name="page">The page to release</param>
  void ReleasePage(ChromiumPage page);

  /// <summary>
  /// Disposes a page and removes it from the pool
  /// </summary>
  /// <param name="page">The page to dispose</param>
  /// <returns>A task representing the disposal operation</returns>
  ValueTask DisposePageAsync(ChromiumPage page);
}
