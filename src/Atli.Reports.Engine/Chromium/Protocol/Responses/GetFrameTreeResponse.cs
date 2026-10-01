using System.Text.Json.Serialization;
using Atli.Reports.Engine.Chromium.Protocol.Types;

namespace Atli.Reports.Engine.Chromium.Protocol.Responses;

/// <summary>
/// Response from Page.getFrameTree command
/// </summary>
internal sealed class GetFrameTreeResponse
{
  [JsonPropertyName("frameTree")]
  public FrameTree? FrameTree { get; set; }
}

/// <summary>
/// Serialization context for GetFrameTreeResponse
/// </summary>
[JsonSerializable(typeof(DevToolsResponse<GetFrameTreeResponse>))]
internal sealed partial class GetFrameTreeResponseSerializationContext : JsonSerializerContext { }
