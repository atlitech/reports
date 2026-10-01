using Atli.Reports.Blazor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Blazor.Services.BrowserServices.Factories;

internal sealed class BrowserPageFactory(
  ILogger<BrowserPage> logger,
  IOptions<BlazorReportsOptions> options,
  IConnectionFactory connectionFactory
) : IBrowserPageFactory
{
  /// <summary>
  /// Creates a new browser page
  /// </summary>
  /// <param name="targetId"> The target id </param>
  /// <param name="pageUri"> The page uri </param>
  /// <returns> The browser page </returns>
  public async ValueTask<BrowserPage> CreateBrowserPage(string targetId, Uri pageUri)
  {
    var pageConnection = await connectionFactory.CreateConnection(
      pageUri,
      options.Value.BrowserOptions.ResponseTimeout
    );
    BrowserPage browserPage = new(logger, targetId, pageConnection);
    return browserPage;
  }
}
