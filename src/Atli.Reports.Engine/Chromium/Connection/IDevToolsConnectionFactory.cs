namespace Atli.Reports.Engine.Chromium.Connection;

/// <summary>
/// Factory for creating DevTools connections
/// </summary>
internal interface IDevToolsConnectionFactory
{
  /// <summary>
  /// Creates a new connection to Chrome DevTools Protocol
  /// </summary>
  /// <param name="uri">The WebSocket URI of the Chrome DevTools endpoint</param>
  /// <param name="responseTimeout">The response timeout for DevTools commands</param>
  /// <returns>The connection</returns>
  ValueTask<DevToolsConnection> CreateConnection(Uri uri, TimeSpan responseTimeout);
}
