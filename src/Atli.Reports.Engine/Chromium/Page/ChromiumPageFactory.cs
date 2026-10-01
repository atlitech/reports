using Atli.Reports.Engine.Chromium.Connection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine.Chromium.Page;

/// <summary>
/// Factory for creating browser pages
/// </summary>
internal sealed class ChromiumPageFactory(
  ILogger<ChromiumPage> logger,
  IOptions<ReportsEngineOptions> options,
  IDevToolsConnectionFactory connectionFactory
) : IChromiumPageFactory
{
  /// <summary>
  /// Creates a new browser page
  /// </summary>
  /// <param name="targetId">The target id</param>
  /// <param name="pageUri">The page uri</param>
  /// <returns>The browser page</returns>
  public async ValueTask<ChromiumPage> CreatePage(string targetId, Uri pageUri)
  {
    var pageConnection = await connectionFactory.CreateConnection(
      pageUri,
      options.Value.Browser.CommandTimeout
    );
    ChromiumPage page = new(logger, targetId, pageConnection);
    return page;
  }
}
