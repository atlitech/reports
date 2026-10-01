namespace Atli.Reports.Blazor.Services.BrowserServices.Factories;

/// <summary>
/// Represents a connection factory
/// </summary>
internal interface IConnectionFactory
{
  /// <summary>
  /// Creates a new connection
  /// </summary>
  /// <param name="uri"> The uri of the page</param>
  /// <param name="responseTimeout"> The response timeout</param>
  /// <returns> The connection</returns>
  ValueTask<Connection> CreateConnection(Uri uri, TimeSpan responseTimeout);
}
