namespace Atli.Reports.Provisioner;

/// <summary>Asks a renderer whether it is ready to convert.</summary>
internal interface IReadinessProbe
{
  /// <summary>
  /// Sends one <c>GET {renderer}/health/ready</c>. A renderer still starting answers otherwise, or not
  /// at all; that is an answer, not an error.
  /// </summary>
  /// <param name="renderer">The renderer's base address.</param>
  /// <param name="cancellationToken">Cancels the probe.</param>
  Task<ReadinessAnswer> ProbeAsync(Uri renderer, CancellationToken cancellationToken);
}

/// <summary>What a renderer answered to a readiness probe.</summary>
/// <param name="IsReady">Whether it answered <c>200</c>.</param>
/// <param name="Status">The answer, for messages: <c>HTTP 503</c>, <c>no connection</c>, and so on.</param>
internal readonly record struct ReadinessAnswer(bool IsReady, string Status)
{
  public static ReadinessAnswer Ready { get; } = new(true, "HTTP 200");

  public static ReadinessAnswer NotReady(string status) => new(false, status);
}
