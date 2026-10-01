using System.Text.Json.Serialization;

namespace Atli.Reports.Blazor.Services.BrowserServices.Responses;

/// <summary>
/// Response returned from the browser request Io.read
/// Reference: https://chromedevtools.github.io/devtools-protocol/tot/IO/#method-read
/// </summary>
/// <param name="Base64Encoded"> Set if the data is base64-encoded</param>
/// <param name="Data"> Data that were read.</param>
/// <param name="Eof"> Set if the end-of-file condition occured while reading.</param>
public sealed record IoReadResponse(bool Base64Encoded, string Data, bool Eof);

/// <summary>
/// The serialization context for the IoReadResponse
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserResultResponse<IoReadResponse>))]
internal sealed partial class IoReadResponseSerializationContext : JsonSerializerContext { }
