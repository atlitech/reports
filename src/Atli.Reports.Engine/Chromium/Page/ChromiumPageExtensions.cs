using System.Text.Json.Serialization.Metadata;
using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Protocol.Messages;

namespace Atli.Reports.Engine.Chromium.Page;

/// <summary>
/// Extensions for ChromiumPage that send raw DevTools commands, used for PDF generation
/// </summary>
internal static class ChromiumPageExtensions
{
  /// <summary>
  /// Gets the underlying DevTools connection (internal use only)
  /// </summary>
  internal static DevToolsConnection GetConnection(this ChromiumPage page) => page.Connection;

  /// <summary>
  /// Sends a DevTools command with typed response handling (internal use for PDF generation)
  /// </summary>
  public static async ValueTask<TResponse> SendDevToolsCommandAsync<TResponse>(
    this ChromiumPage page,
    DevToolsMessage message,
    JsonTypeInfo<TResponse> jsonTypeInfo,
    CancellationToken ct = default
  )
  {
    var connection = GetConnection(page);
    await connection.ConnectAsync(ct);
    return await connection.SendAsync(message, jsonTypeInfo, ct);
  }

  /// <summary>
  /// Sends a DevTools command with response handler (internal use for PDF generation)
  /// </summary>
  public static async ValueTask<TR> SendDevToolsCommandAsync<T, TR>(
    this ChromiumPage page,
    DevToolsMessage message,
    JsonTypeInfo<T> jsonTypeInfo,
    Func<T, Task<TR>> responseHandler,
    CancellationToken ct = default
  )
  {
    var connection = GetConnection(page);
    await connection.ConnectAsync(ct);
    return await connection.SendAsync(message, jsonTypeInfo, responseHandler, ct);
  }

  /// <summary>
  /// Sends a DevTools command without waiting for response (fire-and-forget)
  /// </summary>
  public static void SendDevToolsCommand(this ChromiumPage page, DevToolsMessage message)
  {
    var connection = GetConnection(page);
    connection.SendAsync(message);
  }
}
