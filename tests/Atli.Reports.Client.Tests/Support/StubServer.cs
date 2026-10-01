using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Client.Tests.Support;

/// <summary>
/// A reports client whose <see cref="HttpClient"/> talks to a stub handler instead of a network: the
/// whole client pipeline (resilience handler included) runs, and the stub answers. The pipeline's
/// retry delays and timeouts run on <see cref="Clock"/>, which only moves when a test advances it.
/// </summary>
internal sealed class StubServer : IAsyncDisposable
{
  public static readonly Uri Endpoint = new("http://reports.test:8080/");

  private readonly ServiceProvider _provider;

  private StubServer(ServiceProvider provider, StubHandler handler, TestClock clock)
  {
    _provider = provider;
    Handler = handler;
    Clock = clock;
  }

  public StubHandler Handler { get; }

  public TestClock Clock { get; }

  public IHtmlToPdfConverter Converter => _provider.GetRequiredService<IHtmlToPdfConverter>();

  public HealthCheckService HealthChecks => _provider.GetRequiredService<HealthCheckService>();

  public IServiceProvider Services => _provider;

  /// <summary>
  /// Registers the client with <paramref name="configure"/>d settings over a stub that answers each
  /// request with <paramref name="respond"/>.
  /// </summary>
  /// <param name="respond">Answers each request.</param>
  /// <param name="configure">Adjusts the client settings.</param>
  /// <param name="servicesBefore">Registers services before the client, as an app's defaults would.</param>
  /// <param name="servicesAfter">Registers services after the client.</param>
  public static StubServer Start(
    Func<StubRequest, CancellationToken, Task<HttpResponseMessage>> respond,
    Action<ReportsClientSettings>? configure = null,
    Action<IServiceCollection>? servicesBefore = null,
    Action<IServiceCollection>? servicesAfter = null
  )
  {
    StubHandler handler = new(respond);
    ReportsClientSettings settings = new() { Endpoint = Endpoint };
    configure?.Invoke(settings);

    TestClock clock = new();
    ServiceCollection services = new();
    services.AddLogging();
    // Polly's dependency injection builds every pipeline on the container's TimeProvider.
    services.AddSingleton<TimeProvider>(clock);
    servicesBefore?.Invoke(services);
    services.AddReportsClient(settings).ConfigurePrimaryHttpMessageHandler(() => handler);
    servicesAfter?.Invoke(services);
    return new StubServer(services.BuildServiceProvider(), handler, clock);
  }

  /// <summary>
  /// A stub that answers every request with <paramref name="status"/>, and for errors with problem
  /// details whose <c>kind</c> is <paramref name="kind"/>.
  /// </summary>
  public static StubServer Answering(
    HttpStatusCode status,
    string? kind = null,
    string? retryAfter = null,
    Action<ReportsClientSettings>? configure = null
  ) => Start((_, _) => Task.FromResult(Problem(status, kind, retryAfter)), configure);

  public static HttpResponseMessage Pdf(byte[] body)
  {
    ByteArrayContent content = new(body);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
  }

  public static HttpResponseMessage Problem(
    HttpStatusCode status,
    string? kind,
    string? retryAfter = null
  )
  {
    var kindMember = kind is null ? "" : $",\"kind\":\"{kind}\"";
    HttpResponseMessage response = new(status)
    {
      Content = new StringContent(
        $"{{\"title\":\"A title.\",\"status\":{(int)status},\"detail\":\"It failed.\"{kindMember}}}",
        Encoding.UTF8,
        "application/problem+json"
      ),
    };
    if (retryAfter is not null)
    {
      response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
    }

    return response;
  }

  public async ValueTask DisposeAsync()
  {
    // Disposing a pipeline, Polly waits for the executions it is still running, on the pipeline's
    // clock, for up to 30 seconds. A conversion a failed test left running would hold the disposal
    // until the test timed out, hiding its error; moving the clock on lets Polly give up.
    var disposal = _provider.DisposeAsync().AsTask();
    while (!disposal.IsCompleted)
    {
      Clock.Advance(TimeSpan.FromSeconds(1));
      await Task.Yield();
    }

    await disposal;
  }
}

/// <summary>
/// A request the stub received, with its body read.
/// </summary>
internal sealed record StubRequest(HttpMethod Method, Uri Uri, string? MediaType, string Body);

/// <summary>
/// Answers requests with a callback and records them.
/// </summary>
internal sealed class StubHandler(
  Func<StubRequest, CancellationToken, Task<HttpResponseMessage>> respond
) : HttpMessageHandler
{
  public ConcurrentQueue<StubRequest> Requests { get; } = new();

  protected override async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request,
    CancellationToken cancellationToken
  )
  {
    var body = request.Content is null
      ? ""
      : await request.Content.ReadAsStringAsync(cancellationToken);
    StubRequest received = new(
      request.Method,
      request.RequestUri!,
      request.Content?.Headers.ContentType?.MediaType,
      body
    );
    Requests.Enqueue(received);
    var response = await respond(received, cancellationToken);
    response.RequestMessage = request;
    return response;
  }
}
