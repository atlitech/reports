using System.Net;
using System.Net.Http.Json;

namespace Atli.Reports.Client;

public class BlazorReportsClient : IBlazorReportsClient
{
  private const string BlazorReportsClientName = "BlazorReports";
  private readonly IHttpClientFactory _httpClientFactory;

  public BlazorReportsClient(IHttpClientFactory httpClientFactory)
  {
    _httpClientFactory = httpClientFactory;
  }

  public async Task<Stream> GetReport(
    string reportName,
    CancellationToken cancellationToken = default
  )
  {
    var httpClient = _httpClientFactory.CreateClient(BlazorReportsClientName);
    var response = await httpClient.PostAsync(
      $"reports/{reportName}",
      null,
      cancellationToken: cancellationToken
    );
    if (response is { StatusCode: >= HttpStatusCode.BadRequest })
    {
      throw new HttpRequestException(
        $"Report {reportName} failed with status code {response.StatusCode}"
      );
    }

    return await response.Content.ReadAsStreamAsync(cancellationToken);
  }

  public async Task<Stream> GetReport<T>(
    string reportName,
    T reportData,
    CancellationToken cancellationToken = default
  )
  {
    var httpClient = _httpClientFactory.CreateClient(BlazorReportsClientName);
    var response = await httpClient.PostAsJsonAsync(
      $"reports/{reportName}",
      reportData,
      cancellationToken: cancellationToken
    );
    if (response is { StatusCode: >= HttpStatusCode.BadRequest })
    {
      throw new HttpRequestException(
        $"Report {reportName} failed with status code {response.StatusCode}"
      );
    }

    return await response.Content.ReadAsStreamAsync(cancellationToken);
  }
}
