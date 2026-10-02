using System.Net;
using Atli.Reports.Client.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Client.Tests.Http;

public class AuthenticationTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Api_keys_authenticate_conversions_but_are_absent_from_health_requests_and_bodies()
  {
    const string credential = "caller.test-secret";
    await using var server = StubServer.Start(
      (_, _) => Task.FromResult(StubServer.Pdf("%PDF-1.7 %%EOF"u8.ToArray())),
      settings => settings.ApiKey = credential
    );

    var result = await server.Converter.ConvertAsync("<p>hello</p>", Stream.Null, null, TestToken);
    await server.HealthChecks.CheckHealthAsync(TestToken);

    await Assert.That(result.IsT0).IsTrue();
    var requests = server.Handler.Requests.ToArray();
    await Assert.That(requests[0].Headers["X-Reports-Api-Key"]).IsEquivalentTo([credential]);
    await Assert.That(requests[0].Body).DoesNotContain(credential);
    await Assert.That(requests[0].Uri.AbsoluteUri).DoesNotContain(credential);
    await Assert.That(requests[1].Headers.ContainsKey("X-Reports-Api-Key")).IsFalse();
  }

  [Test]
  public async Task Tokens_are_acquired_per_attempt_and_not_for_health_probes()
  {
    var acquired = 0;
    var attempts = 0;
    await using var server = StubServer.Start(
      (_, _) =>
        Task.FromResult(
          ++attempts == 1
            ? StubServer.Problem(HttpStatusCode.ServiceUnavailable, "Busy", retryAfter: "0")
            : StubServer.Pdf("%PDF-1.7 %%EOF"u8.ToArray())
        ),
      settings =>
        settings.AccessTokenProvider = cancellationToken =>
        {
          cancellationToken.ThrowIfCancellationRequested();
          return ValueTask.FromResult($"token-{++acquired}");
        }
    );

    var result = await server.Converter.ConvertAsync("<p>hello</p>", Stream.Null, null, TestToken);
    await server.HealthChecks.CheckHealthAsync(TestToken);

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(acquired).IsEqualTo(2);
    var requests = server.Handler.Requests.ToArray();
    await Assert.That(requests[0].Headers["Authorization"]).IsEquivalentTo(["Bearer token-1"]);
    await Assert.That(requests[1].Headers["Authorization"]).IsEquivalentTo(["Bearer token-2"]);
    await Assert.That(requests[2].Headers.ContainsKey("Authorization")).IsFalse();
  }

  [Test]
  [Arguments(HttpStatusCode.Unauthorized, ConversionErrorKind.Unauthorized)]
  [Arguments(HttpStatusCode.Forbidden, ConversionErrorKind.Forbidden)]
  public async Task Authentication_failures_are_not_retried(
    HttpStatusCode status,
    ConversionErrorKind expected
  )
  {
    var acquired = 0;
    await using var server = StubServer.Answering(
      status,
      configure: settings =>
        settings.AccessTokenProvider = _ => ValueTask.FromResult($"token-{++acquired}")
    );

    var result = await server.Converter.ConvertAsync("<p>hello</p>", Stream.Null, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(expected);
    await Assert.That(server.Handler.Requests.Count).IsEqualTo(1);
    await Assert.That(acquired).IsEqualTo(1);
  }

  [Test]
  public async Task Provider_failures_are_sanitized_and_do_not_send_or_retry_a_request()
  {
    await using var server = StubServer.Start(
      (_, _) => throw new InvalidOperationException("Must not send."),
      settings =>
        settings.AccessTokenProvider = _ => throw new HttpRequestException("sensitive-token")
    );

    var result = await server.Converter.ConvertAsync("<p>hello</p>", Stream.Null, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Unauthorized);
    await Assert.That(result.AsT1.Message).DoesNotContain("sensitive-token");
    await Assert.That(result.AsT1.Exception).IsNull();
    await Assert.That(server.Handler.Requests).IsEmpty();
  }

  [Test]
  [Arguments("")]
  [Arguments("Bearer token")]
  [Arguments("token\r\nInjected: header")]
  public async Task Invalid_provider_tokens_fail_before_sending(string token)
  {
    await using var server = StubServer.Start(
      (_, _) => throw new InvalidOperationException("Must not send."),
      settings => settings.AccessTokenProvider = _ => ValueTask.FromResult(token)
    );

    var result = await server.Converter.ConvertAsync("<p>hello</p>", Stream.Null, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Unauthorized);
    await Assert.That(server.Handler.Requests).IsEmpty();
  }

  [Test]
  public async Task Redirects_do_not_disclose_the_credential_or_document_to_another_origin()
  {
    var receiverBuilder = WebApplication.CreateSlimBuilder();
    receiverBuilder.Logging.ClearProviders();
    receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var receiver = receiverBuilder.Build();
    var leakedRequests = 0;
    receiver.MapPost(
      "/convert",
      () =>
      {
        Interlocked.Increment(ref leakedRequests);
        return Results.Ok();
      }
    );
    await receiver.StartAsync(TestToken);

    var redirectBuilder = WebApplication.CreateSlimBuilder();
    redirectBuilder.Logging.ClearProviders();
    redirectBuilder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var redirect = redirectBuilder.Build();
    redirect.MapPost(
      "/convert",
      () => Results.Redirect(receiver.Urls.First() + "/convert", preserveMethod: true)
    );
    await redirect.StartAsync(TestToken);

    var host = Host.CreateApplicationBuilder();
    host.Services.AddReportsClient(
      new ReportsClientSettings
      {
        Endpoint = new Uri(redirect.Urls.First()),
        ApiKey = "caller.test-secret",
      }
    );
    using var clientHost = host.Build();
    var result = await clientHost
      .Services.GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync("<p>sensitive document</p>", Stream.Null, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert.That(leakedRequests).IsEqualTo(0);
  }
}
