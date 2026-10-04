using System.Net;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>The HTTP readiness probe: <c>/health/ready</c>, and anything but <c>200</c> is "not yet".</summary>
public class HttpReadinessProbeTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [Arguments("https://sandbox-1-8080.example.test/")]
  [Arguments("https://sandbox-1-8080.example.test")]
  public async Task Asks_the_ready_endpoint_without_credentials(string renderer)
  {
    using Handler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
    using HttpClient client = new(handler, disposeHandler: false);

    var answer = await new HttpReadinessProbe(client).ProbeAsync(new Uri(renderer), TestToken);

    await Assert.That(answer.IsReady).IsTrue();
    await Assert.That(handler.Requests).HasSingleItem();
    var request = handler.Requests[0];
    await Assert
      .That(request.RequestUri)
      .IsEqualTo(new Uri("https://sandbox-1-8080.example.test/health/ready"));
    await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
    await Assert.That(request.Headers.Contains("X-Reports-Api-Key")).IsFalse();
    await Assert.That(request.Headers.Authorization).IsNull();
  }

  [Test]
  [Arguments(HttpStatusCode.Forbidden, "HTTP 403")]
  [Arguments(HttpStatusCode.BadGateway, "HTTP 502")]
  [Arguments(HttpStatusCode.ServiceUnavailable, "HTTP 503")]
  public async Task Any_other_answer_is_not_ready(HttpStatusCode status, string expected)
  {
    using Handler handler = new(_ => new HttpResponseMessage(status));
    using HttpClient client = new(handler, disposeHandler: false);

    var answer = await new HttpReadinessProbe(client).ProbeAsync(
      new Uri("https://sandbox-1-8080.example.test/"),
      TestToken
    );

    await Assert.That(answer.IsReady).IsFalse();
    await Assert.That(answer.Status).IsEqualTo(expected);
  }

  [Test]
  public async Task No_connection_is_not_ready()
  {
    using Handler handler = new(_ =>
      throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused")
    );
    using HttpClient client = new(handler, disposeHandler: false);

    var answer = await new HttpReadinessProbe(client).ProbeAsync(
      new Uri("https://sandbox-1-8080.example.test/"),
      TestToken
    );

    await Assert.That(answer.IsReady).IsFalse();
    await Assert.That(answer.Status).IsEqualTo("no response (ConnectionError)");
  }

  private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
  {
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken
    )
    {
      Requests.Add(request);
      return Task.FromResult(respond(request));
    }
  }
}
