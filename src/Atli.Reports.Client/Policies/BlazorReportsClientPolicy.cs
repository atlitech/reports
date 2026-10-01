using System.Net;
using Polly;
using Polly.Extensions.Http;

namespace Atli.Reports.Client.Policies;

internal static class BlazorReportsClientPolicy
{
  public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
  {
    return HttpPolicyExtensions
      .HandleTransientHttpError()
      .OrResult(message => message.StatusCode == HttpStatusCode.NotFound)
      .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
  }

  public static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
  {
    return HttpPolicyExtensions
      .HandleTransientHttpError()
      .CircuitBreakerAsync(3, TimeSpan.FromSeconds(15));
  }
}
