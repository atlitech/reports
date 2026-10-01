using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Atli.Reports.Benchmarks.Load.Targets;

namespace Atli.Reports.Benchmarks.Load.Runner;

/// <summary>
/// The outcome of one request.
/// </summary>
/// <param name="Start">When the request was sent (<see cref="Stopwatch.GetTimestamp"/>).</param>
/// <param name="End">When the last response byte arrived or the request failed.</param>
/// <param name="Outcome"><see cref="Outcomes.Ok"/>, or the error class.</param>
/// <param name="Pages">The PDF page count, or -1 when there is no valid PDF.</param>
/// <param name="Bytes">The response body size.</param>
/// <param name="Detail">For a failure, the start of the error body or the exception message.</param>
internal readonly record struct RequestSample(
  long Start,
  long End,
  string Outcome,
  int Pages,
  int Bytes,
  string? Detail = null
)
{
  public bool Succeeded => Outcome == Outcomes.Ok;

  public double LatencyMilliseconds => Stopwatch.GetElapsedTime(Start, End).TotalMilliseconds;
}

/// <summary>
/// Request outcome classes, as they appear in the results.
/// </summary>
internal static class Outcomes
{
  /// <summary>
  /// HTTP 2xx with a complete PDF.
  /// </summary>
  public const string Ok = "ok";

  /// <summary>
  /// No response within the client timeout.
  /// </summary>
  public const string Timeout = "client_timeout";

  /// <summary>
  /// HTTP 2xx whose body is not a complete PDF.
  /// </summary>
  public const string InvalidPdf = "invalid_pdf";

  public static string Http(int status) => $"http_{status}";

  public static bool IsTransport(string outcome) =>
    outcome.StartsWith("transport_", StringComparison.Ordinal);
}

/// <summary>
/// One measurement: a fixed number of closed-loop workers sending the same request.
/// </summary>
/// <param name="Concurrency">Workers; each sends its next request as soon as the previous one finishes.</param>
/// <param name="StopIssuing">After this timestamp no worker starts a new request; requests in flight finish.</param>
/// <param name="RequestTimeout">The client-side deadline of each request.</param>
internal sealed record LoadPlan(
  Uri BaseAddress,
  PreparedRequest Request,
  int Concurrency,
  long StopIssuing,
  TimeSpan RequestTimeout
);

/// <summary>
/// Drives a target with closed-loop workers and records every request.
/// </summary>
internal sealed class LoadRunner(HttpClient client)
{
  /// <summary>
  /// How long a worker pauses after a connection-level failure, so a crashed target is not hammered
  /// in a tight loop that would inflate the error count.
  /// </summary>
  private static readonly TimeSpan TransportBackoff = TimeSpan.FromMilliseconds(250);

  /// <summary>
  /// How much of an error body or message to keep with a failed request.
  /// </summary>
  private const int DetailLength = 240;

  public async Task<IReadOnlyList<RequestSample>> RunAsync(
    LoadPlan plan,
    CancellationToken cancellationToken
  )
  {
    var workers = Enumerable
      .Range(0, plan.Concurrency)
      .Select(_ => Task.Run(() => WorkerAsync(plan, cancellationToken), cancellationToken))
      .ToArray();
    var perWorker = await Task.WhenAll(workers);
    return [.. perWorker.SelectMany(samples => samples).OrderBy(sample => sample.Start)];
  }

  /// <summary>
  /// Sends a single request outside any measurement, for smoke checks.
  /// </summary>
  public Task<RequestSample> SendOnceAsync(
    Uri baseAddress,
    PreparedRequest request,
    TimeSpan timeout,
    CancellationToken cancellationToken
  ) => SendAsync(baseAddress, request, timeout, cancellationToken);

  private async Task<List<RequestSample>> WorkerAsync(
    LoadPlan plan,
    CancellationToken cancellationToken
  )
  {
    List<RequestSample> samples = [];
    while (
      !cancellationToken.IsCancellationRequested && Stopwatch.GetTimestamp() < plan.StopIssuing
    )
    {
      var sample = await SendAsync(
        plan.BaseAddress,
        plan.Request,
        plan.RequestTimeout,
        cancellationToken
      );
      samples.Add(sample);
      if (Outcomes.IsTransport(sample.Outcome))
      {
        await Task.Delay(TransportBackoff, cancellationToken);
      }
    }

    return samples;
  }

  private async Task<RequestSample> SendAsync(
    Uri baseAddress,
    PreparedRequest request,
    TimeSpan timeout,
    CancellationToken cancellationToken
  )
  {
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(timeout);
    var start = Stopwatch.GetTimestamp();
    try
    {
      using var message = request.CreateMessage(baseAddress);
      using var response = await client.SendAsync(
        message,
        HttpCompletionOption.ResponseHeadersRead,
        deadline.Token
      );
      var body = await response.Content.ReadAsByteArrayAsync(deadline.Token);
      var end = Stopwatch.GetTimestamp();
      if (!response.IsSuccessStatusCode)
      {
        return new RequestSample(
          start,
          end,
          Outcomes.Http((int)response.StatusCode),
          -1,
          body.Length,
          Excerpt(Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, DetailLength * 2)))
        );
      }

      var pages = PdfInspector.CountPages(body);
      return pages is { } count
        ? new RequestSample(start, end, Outcomes.Ok, count, body.Length)
        : new RequestSample(
          start,
          end,
          Outcomes.InvalidPdf,
          -1,
          body.Length,
          Excerpt(Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, DetailLength)))
        );
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      return new RequestSample(
        start,
        Stopwatch.GetTimestamp(),
        Outcomes.Timeout,
        -1,
        0,
        $"no response within {timeout.TotalSeconds:0} s"
      );
    }
    catch (HttpRequestException exception)
    {
      return new RequestSample(
        start,
        Stopwatch.GetTimestamp(),
        Classify(exception),
        -1,
        0,
        Excerpt(exception.Message)
      );
    }
    catch (IOException exception)
    {
      return new RequestSample(
        start,
        Stopwatch.GetTimestamp(),
        Classify(exception),
        -1,
        0,
        Excerpt(exception.Message)
      );
    }
  }

  private static string Excerpt(string text)
  {
    var flat = string.Join(
        ' ',
        text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
      )
      .Trim();
    return flat.Length <= DetailLength ? flat : flat[..DetailLength] + "…";
  }

  private static string Classify(Exception exception)
  {
    for (var inner = exception; inner is not null; inner = inner.InnerException)
    {
      if (inner is SocketException socket)
      {
        return "transport_" + ToSnakeCase(socket.SocketErrorCode.ToString());
      }
    }

    return exception is HttpRequestException http
      ? "transport_" + ToSnakeCase(http.HttpRequestError.ToString())
      : "transport_io_error";
  }

  private static string ToSnakeCase(string pascal) =>
    string.Concat(
      pascal.Select(
        (c, i) =>
          i > 0 && char.IsUpper(c)
            ? "_" + char.ToLowerInvariant(c)
            : char.ToLowerInvariant(c).ToString()
      )
    );
}
