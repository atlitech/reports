using System.Text.Json.Serialization;

namespace Atli.Reports.Blazor.Services.BrowserServices.Responses;

/// <summary>
/// Response returned from the browser request page.printToPdf
/// Reference: https://chromedevtools.github.io/devtools-protocol/tot/Page/#method-printToPDF
/// </summary>
/// <param name="Data"> Base64-encoded pdf data. Empty if |returnAsStream| is specified. (Encoded as a base64 string when passed over JSON)</param>
/// <param name="Stream">A handle of the stream that holds resulting PDF data.</param>
public sealed record PagePrintToPdfResponse(string Data, string Stream);

/// <summary>
/// The serialization context for the PagePrintToPdfResponse
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserResultResponse<PagePrintToPdfResponse>))]
internal sealed partial class PagePrintToPdfResponseSerializationContext : JsonSerializerContext { }
