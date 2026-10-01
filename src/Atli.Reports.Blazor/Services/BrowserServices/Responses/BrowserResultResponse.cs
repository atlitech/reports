namespace Atli.Reports.Blazor.Services.BrowserServices.Responses;

/// <summary>
/// Response returned from the browser request
/// </summary>
/// <param name="Result"> The result of the request</param>
/// <typeparam name="T"> The type of the result</typeparam>
public record BrowserResultResponse<T>(T Result);
