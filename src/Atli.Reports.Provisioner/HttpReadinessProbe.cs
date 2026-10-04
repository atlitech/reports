using System.Net;

namespace Atli.Reports.Provisioner;

/// <summary>
/// Probes <c>/health/ready</c> over HTTP, without credentials: the server answers health probes
/// anonymously. While a new sandbox starts, the platform's proxy answers <c>403</c> (not running),
/// <c>502</c>, or <c>503</c>, and the server itself <c>503</c> until its browser is up.
/// </summary>
/// <param name="httpClient">The client to send probes with.</param>
internal sealed class HttpReadinessProbe(HttpClient httpClient) : IReadinessProbe
{
  /// <summary>How long one probe may take before it counts as unanswered.</summary>
  public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

  public async Task<ReadinessAnswer> ProbeAsync(Uri renderer, CancellationToken cancellationToken)
  {
    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    attempt.CancelAfter(AttemptTimeout);
    try
    {
      using var response = await httpClient.GetAsync(
        ReadyUri(renderer),
        HttpCompletionOption.ResponseHeadersRead,
        attempt.Token
      );
      return response.StatusCode == HttpStatusCode.OK
        ? ReadinessAnswer.Ready
        : ReadinessAnswer.NotReady((int)response.StatusCode);
    }
    catch (HttpRequestException exception)
    {
      return ReadinessAnswer.NotReady($"no response ({exception.HttpRequestError})");
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      return ReadinessAnswer.NotReady($"no response within {AttemptTimeout.TotalSeconds:0} s");
    }
  }

  /// <summary>The readiness endpoint under <paramref name="renderer"/>, with or without a final slash.</summary>
  public static Uri ReadyUri(Uri renderer)
  {
    ArgumentNullException.ThrowIfNull(renderer);
    return new Uri(renderer.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/health/ready");
  }
}
