using System.Text.Json.Serialization;

namespace Atli.Reports.Blazor.Services.BrowserServices.Requests;

/// <summary>
/// Message sent to the browser
/// </summary>
/// <param name="method"> The method executed by the browser</param>
internal sealed class BrowserMessage(string method)
{
  /// <summary>
  /// The id of the message
  /// </summary>
  public int Id { get; set; }

  /// <summary>
  /// The method executed by the browser
  /// </summary>
  public string Method { get; } = method;

  /// <summary>
  /// The parameters that we want to feed into the browser
  /// </summary>
  [JsonPropertyName("params")]
  public Dictionary<string, object> Parameters { get; } = [];
}

/// <summary>
/// The serialization context for the PageGetFrameTreeResponse
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserMessage))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(double))]
internal sealed partial class BrowserMessageSerializationContext : JsonSerializerContext { }
