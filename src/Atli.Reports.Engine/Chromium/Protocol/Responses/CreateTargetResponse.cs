using System.Text.Json.Serialization;

namespace Atli.Reports.Engine.Chromium.Protocol.Responses;

/// <summary>
/// Response from Target.createTarget command
/// </summary>
internal sealed class CreateTargetResponse
{
  [JsonPropertyName("targetId")]
  public string TargetId { get; set; } = string.Empty;
}

/// <summary>
/// Serialization context for CreateTargetResponse
/// </summary>
[JsonSerializable(typeof(DevToolsResponse<CreateTargetResponse>))]
internal sealed partial class CreateTargetResponseSerializationContext : JsonSerializerContext { }
