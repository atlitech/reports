using System.Text.Json.Serialization;

namespace Atli.Reports.Engine.Chromium.Protocol.Types;

/// <summary>
/// Represents a browser frame
/// </summary>
internal sealed class Frame
{
  [JsonPropertyName("id")]
  public string Id { get; set; } = string.Empty;

  [JsonPropertyName("loaderId")]
  public string LoaderId { get; set; } = string.Empty;

  [JsonPropertyName("url")]
  public string Url { get; set; } = string.Empty;

  [JsonPropertyName("domainAndRegistry")]
  public string DomainAndRegistry { get; set; } = string.Empty;

  [JsonPropertyName("securityOrigin")]
  public string SecurityOrigin { get; set; } = string.Empty;

  [JsonPropertyName("mimeType")]
  public string MimeType { get; set; } = string.Empty;

  [JsonPropertyName("secureContextType")]
  public string SecureContextType { get; set; } = string.Empty;

  [JsonPropertyName("crossOriginIsolatedContextType")]
  public string CrossOriginIsolatedContextType { get; set; } = string.Empty;

  [JsonPropertyName("gatedAPIFeatures")]
  public string[] GatedAPIFeatures { get; set; } = [];
}
