using System.Text.Json.Serialization;

namespace Atli.Reports.Blazor.Services.BrowserServices.Responses;

/// <summary>
/// Response returned from the createTarget request
/// Reference: https://chromedevtools.github.io/devtools-protocol/tot/Target/#method-createTarget
/// </summary>
/// <param name="TargetId"> The id of the page opened.</param>
public sealed record CreateTargetResponse(string TargetId);

/// <summary>
/// The serialization context for the CreateTargetResponse
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserResultResponse<CreateTargetResponse>))]
internal sealed partial class CreateTargetResponseSerializationContext : JsonSerializerContext { }
