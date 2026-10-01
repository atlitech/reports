using System.Text.Json.Serialization;

namespace Atli.Reports.Engine.Chromium.Protocol.Responses;

/// <summary>
/// Response from Page.printToPDF command
/// </summary>
internal sealed class PrintToPdfResponse
{
  [JsonPropertyName("stream")]
  public string Stream { get; set; } = string.Empty;

  [JsonPropertyName("data")]
  public string? Data { get; set; }
}

/// <summary>
/// Serialization context for PrintToPdfResponse
/// </summary>
[JsonSerializable(typeof(DevToolsResponse<PrintToPdfResponse>))]
internal sealed partial class PrintToPdfResponseSerializationContext : JsonSerializerContext { }
