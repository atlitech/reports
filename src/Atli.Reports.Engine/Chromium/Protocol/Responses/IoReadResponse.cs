using System.Text.Json.Serialization;

namespace Atli.Reports.Engine.Chromium.Protocol.Responses;

/// <summary>
/// Response from IO.read command
/// </summary>
internal sealed class IoReadResponse
{
  [JsonPropertyName("base64Encoded")]
  public bool Base64Encoded { get; set; }

  [JsonPropertyName("data")]
  public string Data { get; set; } = string.Empty;

  [JsonPropertyName("eof")]
  public bool Eof { get; set; }
}

/// <summary>
/// Serialization context for IoReadResponse
/// </summary>
[JsonSerializable(typeof(DevToolsResponse<IoReadResponse>))]
internal sealed partial class IoReadResponseSerializationContext : JsonSerializerContext { }
