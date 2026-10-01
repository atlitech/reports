using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Chromium.Connection;

/// <summary>
/// Represents a connection factory for creating WebSocket connections to Chrome DevTools
/// </summary>
internal sealed class DevToolsConnectionFactory(ILogger<DevToolsConnection> logger)
  : IDevToolsConnectionFactory
{
  /// <summary>
  /// Creates a new connection to Chrome DevTools Protocol
  /// </summary>
  /// <param name="uri">The WebSocket URI of the Chrome DevTools endpoint</param>
  /// <param name="responseTimeout">The response timeout for DevTools commands</param>
  /// <returns>The connection</returns>
  public async ValueTask<DevToolsConnection> CreateConnection(Uri uri, TimeSpan responseTimeout)
  {
    DevToolsConnection connection = new(uri, responseTimeout, logger);
    await connection.InitializeAsync();
    return connection;
  }
}
