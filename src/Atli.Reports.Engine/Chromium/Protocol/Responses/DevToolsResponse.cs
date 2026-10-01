using System.Text.Json.Serialization;

namespace Atli.Reports.Engine.Chromium.Protocol.Responses;

/// <summary>
/// Generic response from Chrome DevTools Protocol
/// </summary>
internal sealed class DevToolsResponse<T>
{
  [JsonPropertyName("id")]
  public int Id { get; set; }

  [JsonPropertyName("result")]
  public T? Result { get; set; }

  [JsonPropertyName("error")]
  public DevToolsProtocolError? Error { get; set; }
}

/// <summary>
/// Error from Chrome DevTools Protocol (internal response type)
/// </summary>
internal sealed class DevToolsProtocolError
{
  [JsonPropertyName("code")]
  public int Code { get; set; }

  [JsonPropertyName("message")]
  public string Message { get; set; } = string.Empty;
}
