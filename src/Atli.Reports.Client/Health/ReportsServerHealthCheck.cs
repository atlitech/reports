using System.Globalization;
using Atli.Reports.Client.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Client.Health;

/// <summary>
/// Reports whether the reports server is ready, from its <c>/health/ready</c> endpoint.
/// </summary>
/// <remarks>
/// The probe goes through the converter's <see cref="HttpClient"/>, so handlers the app adds to it
/// (authentication, say) apply, but it is never retried: a server that is not ready right now is
/// what the check reports.
/// </remarks>
internal sealed class ReportsServerHealthCheck(IHttpClientFactory httpClientFactory) : IHealthCheck
{
  /// <summary>
  /// The path of the server's readiness endpoint, relative to its endpoint.
  /// </summary>
  public const string ReadyPath = "health/ready";

  public async Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default
  )
  {
    try
    {
      using HttpRequestMessage request = new(HttpMethod.Get, ReadyPath);
      request.Options.Set(ReportsResilience.NoRetry, true);
      using var response = await httpClientFactory
        .CreateClient(ReportsServerConverter.HttpClientName)
        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

      if (response.IsSuccessStatusCode)
      {
        return HealthCheckResult.Healthy("The reports server is ready.");
      }

      return new HealthCheckResult(
        context.Registration.FailureStatus,
        string.Create(
          CultureInfo.InvariantCulture,
          $"The reports server is not ready: it answered {(int)response.StatusCode}."
        )
      );
    }
    catch (Exception exception) when (ServerErrors.IsMapped(exception))
    {
      return new HealthCheckResult(
        context.Registration.FailureStatus,
        "The reports server could not be reached.",
        exception
      );
    }
  }
}
