using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;

namespace Atli.Reports.Client.Http;

/// <summary>
/// The resilience pipeline of the reports client: a total timeout, retries, and an attempt timeout.
/// </summary>
internal static class ReportsResilience
{
  /// <summary>
  /// The name of the pipeline on the client's <see cref="HttpClient"/>.
  /// </summary>
  public const string PipelineName = "reports";

  /// <summary>
  /// Set on a request that must not be retried, such as a health probe.
  /// </summary>
  public static readonly HttpRequestOptionsKey<bool> NoRetry = new("Atli.Reports.Client.NoRetry");

  /// <summary>
  /// Adds, from the outside in: <paramref name="totalTimeout"/> across all attempts; up to
  /// <paramref name="maxRetryAttempts"/> retries of a <c>503</c> (after its <c>Retry-After</c>) or a
  /// transport failure (after an exponential back-off from one second, with jitter); and
  /// <paramref name="attemptTimeout"/> per attempt.
  /// </summary>
  public static void Configure(
    ResiliencePipelineBuilder<HttpResponseMessage> pipeline,
    TimeSpan totalTimeout,
    int maxRetryAttempts,
    TimeSpan attemptTimeout
  )
  {
    pipeline.AddTimeout(
      new HttpTimeoutStrategyOptions { Name = "TotalTimeout", Timeout = totalTimeout }
    );

    if (maxRetryAttempts > 0)
    {
      pipeline.AddRetry(
        new HttpRetryStrategyOptions
        {
          MaxRetryAttempts = maxRetryAttempts,
          BackoffType = DelayBackoffType.Exponential,
          UseJitter = true,
          Delay = TimeSpan.FromSeconds(1),
          ShouldRetryAfterHeader = true,
          ShouldHandle = arguments => ValueTask.FromResult(ShouldRetry(arguments)),
        }
      );
    }

    pipeline.AddTimeout(
      new HttpTimeoutStrategyOptions { Name = "AttemptTimeout", Timeout = attemptTimeout }
    );
  }

  /// <summary>
  /// Retries what another attempt can fix: the server had no capacity or no browser (<c>503</c>), or
  /// the request never reached it. Never retries <c>4xx</c> (the request is wrong), <c>500</c> (the
  /// document cannot be rendered), <c>504</c> or an attempt timeout (the conversion is too slow), or
  /// the caller's cancellation.
  /// </summary>
  private static bool ShouldRetry(RetryPredicateArguments<HttpResponseMessage> arguments)
  {
    if (
      arguments.Context.GetRequestMessage() is { } request
      && request.Options.TryGetValue(NoRetry, out var noRetry)
      && noRetry
    )
    {
      return false;
    }

    return arguments.Outcome switch
    {
      { Result.StatusCode: HttpStatusCode.ServiceUnavailable } => true,
      { Exception: HttpRequestException } => true,
      _ => false,
    };
  }
}
