using System.Text.Json.Serialization;

namespace Atli.Reports.Engine.Chromium.Protocol.Types;

/// <summary>
/// Represents a browser frame tree
/// </summary>
internal sealed class FrameTree
{
  [JsonPropertyName("frame")]
  public Frame? Frame { get; set; }

  [JsonPropertyName("childFrames")]
  public FrameTree[]? ChildFrames { get; set; }
}
