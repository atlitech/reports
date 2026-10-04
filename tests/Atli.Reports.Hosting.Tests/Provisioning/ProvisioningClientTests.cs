using System.Diagnostics;
using System.Net;
using Atli.Reports.Hosting.Provisioning;

namespace Atli.Reports.Hosting.Tests.Provisioning;

/// <summary>
/// The gateway's client of the provisioning service: its routes and credential, what each refusal
/// or failure throws, and that nothing but the credential leaves the gateway.
/// </summary>
public class ProvisioningClientTests
{
  private const string Tenant = "app-3f2504e0";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Ensure_puts_to_the_tenants_renderer_and_says_whether_it_created_one(
    bool created
  )
  {
    using TestProvisioning provisioning = new(
      TestProvisioning.Answer(
        HttpStatusCode.OK,
        $$"""{"tenantId":"{{Tenant}}","created":{{(created ? "true" : "false")}}}"""
      )
    );

    var result = await provisioning.Client.EnsureRendererAsync(Tenant, TestToken);

    await Assert.That(result).IsEqualTo(created);
    var request = provisioning.Handler.Requests.Single();
    await Assert.That(request.Method).IsEqualTo(HttpMethod.Put);
    // The base address's path is kept.
    await Assert
      .That(request.RequestUri!.AbsoluteUri)
      .IsEqualTo($"https://provisioner.internal.example.test/api/tenants/{Tenant}/renderer");
    await Assert
      .That(request.Headers.GetValues(ProvisioningApi.ApiKeyHeader).Single())
      .IsEqualTo(TestProvisioning.ApiKey);
    await Assert.That(request.Content).IsNull();
  }

  [Test]
  public async Task Delete_deletes_the_tenants_renderer()
  {
    using TestProvisioning provisioning = new(TestProvisioning.Answer(HttpStatusCode.NoContent));

    await provisioning.Client.DeleteRendererAsync(Tenant, TestToken);

    var request = provisioning.Handler.Requests.Single();
    await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
    await Assert
      .That(request.RequestUri!.AbsolutePath)
      .IsEqualTo($"/api/tenants/{Tenant}/renderer");
    await Assert
      .That(request.Headers.GetValues(ProvisioningApi.ApiKeyHeader).Single())
      .IsEqualTo(TestProvisioning.ApiKey);
  }

  [Test]
  [Arguments(HttpStatusCode.Forbidden, ProvisioningProblemKinds.NotAllowed)]
  [Arguments(HttpStatusCode.TooManyRequests, ProvisioningProblemKinds.QuotaExceeded)]
  [Arguments(HttpStatusCode.TooManyRequests, ProvisioningProblemKinds.RateLimited)]
  [Arguments(HttpStatusCode.ServiceUnavailable, ProvisioningProblemKinds.Failed)]
  public async Task Each_problem_kind_is_carried_with_its_status(HttpStatusCode status, string kind)
  {
    using TestProvisioning provisioning = new(
      TestProvisioning.Problem(status, kind, "The service says why.")
    );

    var ensure = await Assert
      .That(async () => await provisioning.Client.EnsureRendererAsync(Tenant, TestToken))
      .Throws<ProvisioningApiException>();
    var delete = await Assert
      .That(async () => await provisioning.Client.DeleteRendererAsync(Tenant, TestToken))
      .Throws<ProvisioningApiException>();

    await Assert.That(ensure!.Kind).IsEqualTo(kind);
    await Assert.That(ensure.StatusCode).IsEqualTo((int)status);
    await Assert.That(ensure.RetryAfter).IsNull();
    await Assert
      .That(ensure.Message)
      .IsEqualTo(
        $"PUT /tenants/{Tenant}/renderer failed with {(int)status} ({status}), kind {kind}: The service says why."
      );
    await Assert.That(delete!.Kind).IsEqualTo(kind);
    await Assert.That(delete.Message).StartsWith($"DELETE /tenants/{Tenant}/renderer failed");
  }

  [Test]
  public async Task Retry_after_is_read_in_seconds_or_as_a_date()
  {
    var at = new DateTimeOffset(2026, 10, 4, 12, 0, 45, TimeSpan.Zero);
    Func<HttpRequestMessage, HttpResponseMessage>[] answers =
    [
      TestProvisioning.Problem(
        HttpStatusCode.TooManyRequests,
        ProvisioningProblemKinds.RateLimited,
        configure: response => response.Headers.Add("Retry-After", "30")
      ),
      TestProvisioning.Problem(
        HttpStatusCode.TooManyRequests,
        ProvisioningProblemKinds.RateLimited,
        configure: response => response.Headers.RetryAfter = new(at)
      ),
      TestProvisioning.Problem(
        HttpStatusCode.TooManyRequests,
        ProvisioningProblemKinds.RateLimited,
        configure: response => response.Headers.RetryAfter = new(at.AddHours(-1))
      ),
    ];
    TimeSpan?[] expected = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45), TimeSpan.Zero];

    for (var i = 0; i < answers.Length; i++)
    {
      using TestProvisioning provisioning = new(answers[i]);

      var exception = await Assert
        .That(async () => await provisioning.Client.EnsureRendererAsync(Tenant, TestToken))
        .Throws<ProvisioningApiException>();

      await Assert.That(exception!.RetryAfter).IsEqualTo(expected[i]);
    }
  }

  [Test]
  [Arguments("text/html", "<html><body>502 Bad Gateway</body></html>")]
  [Arguments("application/problem+json", "not json")]
  [Arguments("application/problem+json", """["NotAllowed"]""")]
  [Arguments("application/problem+json", """{"kind":"SomethingElse"}""")]
  [Arguments("application/problem+json", """{"kind":7}""")]
  // Problem details that are not declared as such.
  [Arguments("application/json", """{"kind":"NotAllowed"}""")]
  public async Task An_answer_that_is_not_a_known_problem_keeps_its_status_but_has_no_kind(
    string contentType,
    string body
  )
  {
    using TestProvisioning provisioning = new(
      TestProvisioning.Answer(HttpStatusCode.BadGateway, body, contentType)
    );

    var exception = await Assert
      .That(async () => await provisioning.Client.EnsureRendererAsync(Tenant, TestToken))
      .Throws<ProvisioningApiException>();

    await Assert.That(exception!.StatusCode).IsEqualTo(502);
    await Assert.That(exception.Kind).IsNull();
    await Assert
      .That(exception.Message)
      .IsEqualTo($"PUT /tenants/{Tenant}/renderer failed with 502 (BadGateway).");
  }

  [Test]
  public async Task An_error_body_is_read_only_up_to_its_limit()
  {
    var padding = new string(' ', ProvisioningClient.MaxBodyBytes);
    using TestProvisioning provisioning = new(
      TestProvisioning.Answer(
        HttpStatusCode.Forbidden,
        $$"""{"kind":"NotAllowed",{{padding}}"detail":"x"}""",
        "application/problem+json"
      )
    );

    var exception = await Assert
      .That(async () => await provisioning.Client.EnsureRendererAsync(Tenant, TestToken))
      .Throws<ProvisioningApiException>();

    await Assert.That(exception!.StatusCode).IsEqualTo(403);
    await Assert.That(exception.Kind).IsNull();
  }

  [Test]
  public async Task The_services_detail_is_cut_short_and_never_carries_the_key()
  {
    var detail =
      "\u001b[31mEchoed "
      + TestProvisioning.ApiKey
      + " "
      + new string('x', 2 * ProvisioningClient.MaxDetailLength);
    using TestProvisioning provisioning = new(
      TestProvisioning.Problem(
        HttpStatusCode.ServiceUnavailable,
        ProvisioningProblemKinds.Failed,
        detail.Replace("\u001b", "\\u001b", StringComparison.Ordinal)
      )
    );

    var exception = await Assert
      .That(async () => await provisioning.Client.EnsureRendererAsync(Tenant, TestToken))
      .Throws<ProvisioningApiException>();

    await Assert.That(exception!.Message).DoesNotContain(TestProvisioning.ApiKey);
    await Assert.That(exception.Message).DoesNotContain("\u001b");
    await Assert.That(exception.Message).Contains("Echoed [redacted] xxx");
    await Assert
      .That(exception.Message.Length)
      .IsLessThan(ProvisioningClient.MaxDetailLength + 100);
  }

  [Test]
  [Arguments("not json")]
  [Arguments("""{"created":true}""")]
  [Arguments("""{"tenantId":"app-other","created":true}""")]
  public async Task A_success_without_the_tenants_answer_fails(string body)
  {
    using TestProvisioning provisioning = new(TestProvisioning.Answer(HttpStatusCode.OK, body));

    var exception = await Assert
      .That(async () => await provisioning.Client.EnsureRendererAsync(Tenant, TestToken))
      .Throws<ProvisioningApiException>();

    await Assert.That(exception!.StatusCode).IsEqualTo(200);
    await Assert.That(exception.Kind).IsNull();
  }

  [Test]
  public async Task A_service_that_cannot_be_reached_or_does_not_answer_has_no_status()
  {
    Exception[] failures =
    [
      new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"),
      // What HttpClient.Timeout, or a connection that does not open in time, throws.
      new TaskCanceledException("Timed out.", new TimeoutException()),
    ];
    foreach (var failure in failures)
    {
      using TestProvisioning provisioning = new(_ => throw failure);

      var exception = await Assert
        .That(async () => await provisioning.Client.EnsureRendererAsync(Tenant, TestToken))
        .Throws<ProvisioningApiException>();

      await Assert.That(exception!.StatusCode).IsNull();
      await Assert.That(exception.Kind).IsNull();
      await Assert.That(exception.InnerException).IsSameReferenceAs(failure);
      await Assert.That(exception.Message).DoesNotContain(TestProvisioning.ApiKey);
    }
  }

  [Test]
  public async Task The_callers_cancellation_is_not_a_failure()
  {
    using CancellationTokenSource canceled = new();
    await canceled.CancelAsync();
    using TestProvisioning provisioning = new(_ =>
      throw new TaskCanceledException("Canceled.", null, canceled.Token)
    );

    await Assert
      .That(async () => await provisioning.Client.EnsureRendererAsync(Tenant, canceled.Token))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task Invalid_settings_and_tenants_are_refused_without_the_key()
  {
    using HttpClient http = new();
    var badKey = "two words " + TestProvisioning.ApiKey;

    var key = await Assert
      .That(() => new ProvisioningClient(http, new Uri(TestProvisioning.BaseAddress), badKey))
      .Throws<ArgumentException>();
    await Assert
      .That(() =>
        new ProvisioningClient(
          http,
          new Uri("https://provisioner.example.test/?key=1"),
          TestProvisioning.ApiKey
        )
      )
      .Throws<ArgumentException>();
    await Assert
      .That(() =>
        new ProvisioningClient(
          http,
          new Uri("ftp://provisioner.example.test/"),
          TestProvisioning.ApiKey
        )
      )
      .Throws<ArgumentException>();
    await Assert.That(key!.Message).DoesNotContain(TestProvisioning.ApiKey);

    using TestProvisioning provisioning = new(TestProvisioning.Answer(HttpStatusCode.NoContent));
    await Assert
      .That(async () => await provisioning.Client.DeleteRendererAsync("../acme", TestToken))
      .Throws<ArgumentException>();
    await Assert.That(provisioning.Handler.Requests).IsEmpty();
  }

  [Test]
  public async Task A_redirect_is_not_followed()
  {
    await using RawHttpServer elsewhere = new(
      RawHttpServer.Response(
        "200 OK",
        "Content-Type: application/json\r\n",
        $$"""{"tenantId":"{{Tenant}}","created":true}"""
      )
    );
    await using RawHttpServer service = new(
      RawHttpServer.Response(
        "307 Temporary Redirect",
        $"Location: {elsewhere.BaseUrl}/tenants/{Tenant}/renderer\r\n"
      )
    );
    using HttpClient http = new(ProvisioningClient.CreateHandler());
    ProvisioningClient client = new(http, new Uri(service.BaseUrl), TestProvisioning.ApiKey);

    var exception = await Assert
      .That(async () => await client.EnsureRendererAsync(Tenant, TestToken))
      .Throws<ProvisioningApiException>();

    await Assert.That(exception!.StatusCode).IsEqualTo(307);
    await Assert.That(service.Heads).HasSingleItem();
    await Assert.That(elsewhere.Heads).IsEmpty();
  }

  [Test]
  public async Task Only_the_key_is_sent_and_no_trace_context_or_baggage()
  {
    await using RawHttpServer service = new(RawHttpServer.Response("204 No Content"));
    using var caller = new Activity("caller-request").Start();
    caller.AddBaggage("secret", "caller-baggage");
    caller.TraceStateString = "caller=state";

    // A handler that propagates, to show the call runs under the caller's activity...
    using (HttpClient propagating = new(new SocketsHttpHandler()))
    {
      ProvisioningClient control = new(
        propagating,
        new Uri(service.BaseUrl),
        TestProvisioning.ApiKey
      );
      await control.DeleteRendererAsync(Tenant, TestToken);
    }

    // ...and the client's own, which sends none of it.
    using HttpClient http = new(ProvisioningClient.CreateHandler());
    ProvisioningClient client = new(http, new Uri(service.BaseUrl), TestProvisioning.ApiKey);
    await client.DeleteRendererAsync(Tenant, TestToken);

    var heads = service.Heads;
    await Assert.That(heads.Count).IsEqualTo(2);
    await Assert
      .That(
        heads[0].Any(line => line.StartsWith("traceparent:", StringComparison.OrdinalIgnoreCase))
      )
      .IsTrue();
    await Assert.That(heads[1][0]).IsEqualTo($"DELETE /tenants/{Tenant}/renderer HTTP/1.1");
    string[] allowed = ["Host", ProvisioningApi.ApiKeyHeader, "Content-Length"];
    var names = heads[1]
      .Skip(1)
      .Select(line => line[..line.IndexOf(':', StringComparison.Ordinal)]);
    await Assert
      .That(names.Where(name => !allowed.Contains(name, StringComparer.OrdinalIgnoreCase)))
      .IsEmpty();
    await Assert
      .That(heads[1])
      .Contains($"{ProvisioningApi.ApiKeyHeader}: {TestProvisioning.ApiKey}");
  }
}
