using System.Diagnostics;
using System.Net;
using Atli.Reports.Client.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Client.Tests.Http;

/// <summary>
/// What the client retries, and how long it waits first.
/// </summary>
public class RetryTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task A_busy_server_is_retried_after_its_retry_after_delay()
  {
    var attempts = 0;
    await using var server = StubServer.Start(
      (_, _) =>
        Task.FromResult(
          Interlocked.Increment(ref attempts) == 1
            ? StubServer.Problem(HttpStatusCode.ServiceUnavailable, "Busy", retryAfter: "1")
            : StubServer.Pdf("%PDF-1.7 %%EOF"u8.ToArray())
        )
    );
    using MemoryStream destination = new();

    var stopwatch = Stopwatch.StartNew();
    var result = await server.Converter.ConvertAsync("<p>x</p>", destination, null, TestToken);

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(attempts).IsEqualTo(2);
    // Retry-After: 1 means one second; the first back-off without it would be about one second
    // too, so the bound only shows the delay was not skipped.
    await Assert.That(stopwatch.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
    await Assert.That(destination.ToArray()).IsEquivalentTo("%PDF-1.7 %%EOF"u8.ToArray());
  }

  [Test]
  public async Task The_retry_after_delay_is_honored_beyond_the_default_back_off()
  {
    var attempts = 0;
    await using var server = StubServer.Start(
      (_, _) =>
        Task.FromResult(
          Interlocked.Increment(ref attempts) == 1
            ? StubServer.Problem(
              HttpStatusCode.ServiceUnavailable,
              "BrowserUnavailable",
              retryAfter: "3"
            )
            : StubServer.Pdf("%PDF-1.7 %%EOF"u8.ToArray())
        )
    );

    var stopwatch = Stopwatch.StartNew();
    var result = await server.Converter.ConvertAsync("<p>x</p>", Stream.Null, null, TestToken);

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(attempts).IsEqualTo(2);
    // The exponential back-off alone would wait about one second (with jitter, under two).
    await Assert.That(stopwatch.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(2.9));
  }

  [Test]
  public async Task A_server_that_stays_busy_ends_as_busy_once_the_retries_are_spent()
  {
    await using var server = StubServer.Answering(
      HttpStatusCode.ServiceUnavailable,
      "Busy",
      retryAfter: "0",
      configure: settings => settings.MaxRetryAttempts = 2
    );

    var result = await server.Converter.ConvertAsync("<p>x</p>", Stream.Null, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Busy);
    await Assert.That(server.Handler.Requests.Count).IsEqualTo(3);
  }

  [Test]
  public async Task A_transport_failure_is_retried()
  {
    var attempts = 0;
    await using var server = StubServer.Start(
      (_, _) =>
        Interlocked.Increment(ref attempts) == 1
          ? throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused")
          : Task.FromResult(StubServer.Pdf("%PDF-1.7 %%EOF"u8.ToArray()))
    );

    var result = await server.Converter.ConvertAsync("<p>x</p>", Stream.Null, null, TestToken);

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(attempts).IsEqualTo(2);
  }

  [Test]
  [Arguments(HttpStatusCode.BadRequest, "InvalidRequest")]
  [Arguments(HttpStatusCode.UnprocessableEntity, "SignalTimeout")]
  [Arguments(HttpStatusCode.InternalServerError, "RenderFailed")]
  [Arguments(HttpStatusCode.GatewayTimeout, "Timeout")]
  public async Task Answers_another_attempt_cannot_fix_are_not_retried(
    HttpStatusCode status,
    string kind
  )
  {
    await using var server = StubServer.Answering(status, kind);

    var result = await server.Converter.ConvertAsync("<p>x</p>", Stream.Null, null, TestToken);

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(server.Handler.Requests.Count).IsEqualTo(1);
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task The_standard_resilience_handler_of_service_defaults_does_not_apply(
    bool defaultsFirst
  )
  {
    // Aspire's service defaults add AddStandardResilienceHandler to every HttpClient. It would retry
    // the 500 and cut attempts off after 10 seconds.
    Action<IServiceCollection> serviceDefaults = services =>
      services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
    var attempts = 0;
    await using var server = StubServer.Start(
      (_, _) =>
        Task.FromResult(
          Interlocked.Increment(ref attempts) == 1
            ? StubServer.Problem(HttpStatusCode.ServiceUnavailable, "Busy", retryAfter: "0")
            : StubServer.Problem(HttpStatusCode.InternalServerError, "RenderFailed")
        ),
      servicesBefore: defaultsFirst ? serviceDefaults : null,
      servicesAfter: defaultsFirst ? null : serviceDefaults
    );

    var result = await server.Converter.ConvertAsync("<p>x</p>", Stream.Null, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    // The client's own retry took the 503; nothing retried the 500.
    await Assert.That(attempts).IsEqualTo(2);
  }

  [Test]
  public async Task Each_attempt_sends_the_whole_request_again()
  {
    var attempts = 0;
    await using var server = StubServer.Start(
      (_, _) =>
        Task.FromResult(
          Interlocked.Increment(ref attempts) == 1
            ? StubServer.Problem(HttpStatusCode.ServiceUnavailable, "Busy", retryAfter: "0")
            : StubServer.Pdf("%PDF-1.7 %%EOF"u8.ToArray())
        )
    );

    await server.Converter.ConvertAsync("<p>retried</p>", Stream.Null, null, TestToken);

    var bodies = server.Handler.Requests.Select(request => request.Body).ToArray();
    await Assert.That(bodies.Length).IsEqualTo(2);
    await Assert.That(bodies[1]).IsEqualTo(bodies[0]);
    await Assert.That(bodies[1]).Contains("<p>retried</p>").Because("HTML travels unescaped");
  }
}
