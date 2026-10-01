using System.Text.Json.Serialization;

namespace Atli.Reports.Engine.Chromium.Protocol.Messages;

/// <summary>
/// Represents a message sent to Chrome DevTools Protocol
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DevToolsMessage))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Dictionary<string, object>))]
internal sealed partial class DevToolsMessageSerializationContext : JsonSerializerContext;

/// <summary>
/// Message format for Chrome DevTools Protocol
/// </summary>
internal sealed class DevToolsMessage(string method)
{
  [JsonPropertyName("id")]
  public int Id { get; set; }

  [JsonPropertyName("method")]
  public string Method { get; set; } = method;

  [JsonPropertyName("params")]
  public Dictionary<string, object> Parameters { get; set; } = [];
}
