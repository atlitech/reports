using System.Net;
using System.Net.Sockets;
using System.Text;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Hosting.Provisioning;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Atli.Reports.Engine.Tests.Server.Gateway.GatewayHost;
using static Atli.Reports.Engine.Tests.Server.Gateway.OnDemandGateway;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>
/// <c>Provisioning:Mode=OnDemand</c>: the first conversion of a tenant under its caller's prefix,
/// or one whose renderer the platform no longer finds, has the provisioning service create the
/// renderer, once per conversion and once for all of a tenant's concurrent conversions. Listed
/// tenants stay the operator's, and the service's refusals reach the caller as the gateway's own
/// errors.
/// </summary>
public class GatewayProvisioningTests
{
  private const string Tenant = "app-3f2504e0";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task The_first_conversion_of_a_prefix_tenant_creates_its_renderer_and_converts()
  {
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7 created")
    );
    await using var service = await FakeProvisioningService.StartAsync(tenant =>
      Record(tenant, renderer)
    );
    await using var under = await StartAsync(service);

    using HttpRequestMessage request = new(HttpMethod.Post, "/convert")
    {
      Content = new StringContent("""{"html":"<p>x</p>"}""", Encoding.UTF8, "application/json"),
    };
    request.Headers.Add("X-Reports-Tenant", Tenant);
    request.Headers.Add("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    request.Headers.Add("baggage", "secret=caller-baggage");
    using var response = await under.Gateway.Client.SendAsync(request, TestToken);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(await response.Content.ReadAsStringAsync(TestToken))
      .IsEqualTo("%PDF-1.7 created");
    var call = service.Calls.Single();
    await Assert.That(call.Method).IsEqualTo("PUT");
    await Assert.That(call.TenantId).IsEqualTo(Tenant);
    await Assert.That(call.Headers["X-Reports-Api-Key"]).IsEqualTo(FakeProvisioningService.ApiKey);
    // Nothing of the caller's request reaches the service.
    await Assert.That(call.Headers.ContainsKey("traceparent")).IsFalse();
    await Assert.That(call.Headers.ContainsKey("baggage")).IsFalse();
    await Assert.That(call.Headers.ContainsKey("X-Reports-Tenant")).IsFalse();
    await Assert.That(renderer.Requests).HasSingleItem();

    // The record is cached now: the next conversion needs no service.
    using var again = await under.ConvertAsync(Tenant);
    await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(service.Ensures).IsEqualTo(1);

    await Assert.That(under.Logs.WithEventId(64).Single()["TenantId"]).IsEqualTo(Tenant);
    var ensured = under.Logs.WithEventId(65).Single();
    await Assert.That(ensured["TenantId"]).IsEqualTo(Tenant);
    await Assert.That(ensured["Outcome"]).IsEqualTo("created");
    await Assert.That(under.Logs.WithEventId(40)).IsEmpty();
    await Assert
      .That(
        under.Logs.Entries.Any(entry =>
          entry.Message.Contains(FakeProvisioningService.ApiKey, StringComparison.Ordinal)
        )
      )
      .IsFalse();
  }

  [Test]
  public async Task Concurrent_first_conversions_share_one_ensure()
  {
    const int requests = 5;
    DeadlineCounter deadlines = new(TimeSpan.FromSeconds(77));
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    await using var service = await FakeProvisioningService.StartAsync(tenant =>
      Record(tenant, renderer)
    );
    service.OnEnsure = async (context, tenant) =>
    {
      await release.Task.WaitAsync(context.RequestAborted);
      await service.EnsureAsync(context, tenant);
    };
    await using var under = await StartAsync(
      service,
      ["--ReportsServer:Gateway:RendererTimeout=00:01:17"],
      builder => builder.Services.AddSingleton<TimeProvider>(deadlines)
    );

    var sent = Enumerable.Range(0, requests).Select(_ => under.ConvertAsync(Tenant)).ToArray();
    // Each conversion starts its deadline and goes straight on to the ensure, which the service
    // holds until every one of them has joined it.
    var allJoined = await TestEngine.EventuallyAsync(
      () => Task.FromResult(deadlines.Count >= requests && service.Ensures >= 1),
      TestEngine.GenerousTimeout
    );
    await Task.Delay(TimeSpan.FromMilliseconds(250), TestToken);
    release.SetResult();
    var responses = await Task.WhenAll(sent);

    await Assert.That(allJoined).IsTrue();
    foreach (var response in responses)
    {
      using (response)
      {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
      }
    }

    await Assert.That(service.Ensures).IsEqualTo(1);
    await Assert.That(renderer.Requests.Count).IsEqualTo(requests);
    await Assert.That(under.Logs.WithEventId(64)).HasSingleItem();
  }

  [Test]
  public async Task One_conversions_cancellation_does_not_cancel_the_shared_ensure()
  {
    DeadlineCounter deadlines = new(TimeSpan.FromSeconds(77));
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var ensureAborted = false;
    // Captured here: the service runs on Kestrel's threads, outside the test's context.
    var testToken = TestToken;
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    await using var service = await FakeProvisioningService.StartAsync(tenant =>
      Record(tenant, renderer)
    );
    service.OnEnsure = async (context, tenant) =>
    {
      await release.Task.WaitAsync(testToken);
      ensureAborted = context.RequestAborted.IsCancellationRequested;
      await service.EnsureAsync(context, tenant);
    };
    await using var under = await StartAsync(
      service,
      ["--ReportsServer:Gateway:RendererTimeout=00:01:17"],
      builder => builder.Services.AddSingleton<TimeProvider>(deadlines)
    );

    // The first conversion starts the ensure, and its caller gives up.
    using CancellationTokenSource giveUp = new();
    var first = under.ConvertAsync(Tenant, giveUp.Token);
    await TestEngine.EventuallyAsync(
      () => Task.FromResult(service.Ensures == 1),
      TestEngine.GenerousTimeout
    );
    await giveUp.CancelAsync();
    await Assert.That(async () => await first).Throws<OperationCanceledException>();
    var abandoned = await TestEngine.EventuallyAsync(
      () =>
        Task.FromResult(under.Logs.WithEventId(20).Any(entry => (int)entry["StatusCode"]! == 499)),
      TestEngine.GenerousTimeout
    );

    // The second joins the ensure still in flight.
    var second = under.ConvertAsync(Tenant);
    await TestEngine.EventuallyAsync(
      () => Task.FromResult(deadlines.Count >= 2),
      TestEngine.GenerousTimeout
    );
    await Task.Delay(TimeSpan.FromMilliseconds(250), TestToken);
    release.SetResult();
    using var response = await second;

    await Assert.That(abandoned).IsTrue();
    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(service.Ensures).IsEqualTo(1);
    await Assert.That(ensureAborted).IsFalse();
  }

  [Test]
  public async Task A_listed_tenant_without_a_record_gets_no_renderer()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Listed tenants are never created.")
    );
    await using var under = await StartAsync(service);

    var problem = await ReadProblemAsync(await under.ConvertAsync("acme"));

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(problem.Detail).IsEqualTo("The tenant has no renderer.");
    await Assert.That(service.Calls).IsEmpty();
    await Assert.That(under.Logs.WithEventId(40)).HasSingleItem();
  }

  [Test]
  [Arguments(
    403,
    "NotAllowed",
    null,
    "BrowserUnavailable",
    "The tenant's renderer is unavailable.",
    5,
    LogLevel.Error
  )]
  [Arguments(
    429,
    "QuotaExceeded",
    null,
    "BrowserUnavailable",
    "The renderer quota of the tenant's prefix is full. Delete tenants that are no longer used, or ask the operator to raise the quota.",
    5,
    LogLevel.Warning
  )]
  [Arguments(
    429,
    "RateLimited",
    "30",
    "Busy",
    "The provisioning service is at its rate limit. Retry later.",
    30,
    LogLevel.Warning
  )]
  [Arguments(
    429,
    "RateLimited",
    "600",
    "Busy",
    "The provisioning service is at its rate limit. Retry later.",
    60,
    LogLevel.Warning
  )]
  [Arguments(
    429,
    "RateLimited",
    "0",
    "Busy",
    "The provisioning service is at its rate limit. Retry later.",
    1,
    LogLevel.Warning
  )]
  public async Task Refusals_reach_the_caller_as_the_gateways_own_errors(
    int status,
    string kind,
    string? retryAfter,
    string expectedKind,
    string expectedDetail,
    int expectedRetryAfter,
    LogLevel expectedLevel
  )
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Refused.")
    );
    service.OnEnsure = (context, _) =>
      FakeProvisioningService.WriteProblemAsync(context, status, kind, retryAfter);
    await using var under = await StartAsync(service);

    var problem = await ReadProblemAsync(await under.ConvertAsync(Tenant));

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Kind).IsEqualTo(expectedKind);
    await Assert.That(problem.Detail).IsEqualTo(expectedDetail);
    await Assert.That(problem.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(expectedRetryAfter));
    var refused = under.Logs.WithEventId(66).Single();
    await Assert.That(refused.Level).IsEqualTo(expectedLevel);
    await Assert.That(refused["TenantId"]).IsEqualTo(Tenant);
    await Assert.That(refused["Kind"]).IsEqualTo(kind);
    await Assert.That(service.Ensures).IsEqualTo(1);
  }

  [Test]
  public async Task A_failure_or_an_unexpected_answer_makes_the_renderer_unavailable()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Failed.")
    );
    var answers = new Queue<Func<HttpContext, Task>>([
      context => FakeProvisioningService.WriteProblemAsync(context, 503, "Failed"),
      context => FakeProvisioningService.WriteProblemAsync(context, 500, null),
      context =>
      {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        return context.Response.WriteAsync("upstream connect error", context.RequestAborted);
      },
      context => FakeProvisioningService.WriteEnsuredAsync(context, "app-another", true),
    ]);
    var count = answers.Count;
    service.OnEnsure = (context, _) => answers.Dequeue()(context);
    await using var under = await StartAsync(service);

    for (var i = 0; i < count; i++)
    {
      var problem = await ReadProblemAsync(await under.ConvertAsync(Tenant));

      await Assert.That(problem.Status).IsEqualTo(503);
      await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
      await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer could not be created.");
      await Assert.That(problem.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(5));
    }

    await Assert.That(service.Ensures).IsEqualTo(count);
    var failed = under.Logs.WithEventId(67);
    await Assert.That(failed.Count).IsEqualTo(count);
    await Assert.That(failed[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(failed[0]["StatusCode"]).IsEqualTo(503);
    await Assert
      .That(
        under.Logs.Entries.Any(entry =>
          entry.Message.Contains(FakeProvisioningService.ServiceWords, StringComparison.Ordinal)
        )
      )
      .IsFalse();
  }

  [Test]
  public async Task A_gateway_the_service_does_not_admit_gets_no_renderer()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never admitted.")
    );
    const string wrongKey = "gateway.wrong-0123456789abcdefghijklmnopqrstuvwxyz";
    await using var under = await StartAsync(
      service,
      [$"--ReportsServer:Gateway:Provisioning:ApiKey={wrongKey}"]
    );

    var problem = await ReadProblemAsync(await under.ConvertAsync(Tenant));

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer could not be created.");
    await Assert.That(under.Logs.WithEventId(67).Single()["StatusCode"]).IsEqualTo(401);
    await Assert
      .That(
        under.Logs.Entries.Any(entry => entry.Message.Contains(wrongKey, StringComparison.Ordinal))
      )
      .IsFalse();
  }

  [Test]
  public async Task A_service_that_cannot_be_reached_makes_the_renderer_unavailable()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never reached.")
    );
    await using var under = await StartAsync(
      service,
      [$"--ReportsServer:Gateway:Provisioning:Url=http://127.0.0.1:{ClosedPort()}"]
    );

    var problem = await ReadProblemAsync(await under.ConvertAsync(Tenant));

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer could not be created.");
    var failed = under.Logs.WithEventId(67).Single();
    await Assert.That(failed["StatusCode"]).IsNull();
  }

  [Test]
  public async Task An_ensure_is_bounded_by_the_provisioning_timeout()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never answered.")
    );
    service.OnEnsure = (context, _) => Task.Delay(Timeout.Infinite, context.RequestAborted);
    await using var under = await StartAsync(
      service,
      ["--ReportsServer:Gateway:Provisioning:Timeout=00:00:01"]
    );

    var watch = System.Diagnostics.Stopwatch.StartNew();
    var problem = await ReadProblemAsync(await under.ConvertAsync(Tenant));
    watch.Stop();

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer could not be created.");
    await Assert
      .That(watch.Elapsed)
      .IsBetween(TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(20));
    await Assert.That(under.Logs.WithEventId(67)).HasSingleItem();
  }

  [Test]
  public async Task A_conversion_waits_for_an_ensure_only_within_its_renderer_timeout()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never answered.")
    );
    service.OnEnsure = (context, _) => Task.Delay(Timeout.Infinite, context.RequestAborted);
    await using var under = await StartAsync(
      service,
      [
        "--ReportsServer:Gateway:Provisioning:Timeout=00:01:00",
        "--ReportsServer:Gateway:RendererTimeout=00:00:01",
      ]
    );

    var problem = await ReadProblemAsync(await under.ConvertAsync(Tenant));

    await Assert.That(problem.Status).IsEqualTo(504);
    await Assert.That(problem.Kind).IsEqualTo("Timeout");
    await Assert.That(under.Logs.WithEventId(49)).HasSingleItem();
  }

  [Test]
  public async Task A_renderer_the_platform_no_longer_finds_is_replaced_and_the_conversion_resent()
  {
    await using var deleted = await FakeRenderer.StartAsync(NotFoundAsync);
    await using var replacement = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7 replacement")
    );
    await using var service = await FakeProvisioningService.StartAsync(tenant =>
      Record(tenant, replacement)
    );
    // The service finds the record's sandbox gone and records a new renderer.
    service.Store.Records[Tenant] = Record(Tenant, deleted);
    service.OnEnsure = async (context, tenant) =>
    {
      service.Store.Records[tenant] = Record(tenant, replacement);
      await FakeProvisioningService.WriteEnsuredAsync(context, tenant, true);
    };
    await using var under = await StartAsync(service);

    using var response = await under.ConvertAsync(Tenant);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(await response.Content.ReadAsStringAsync(TestToken))
      .IsEqualTo("%PDF-1.7 replacement");
    await Assert.That(deleted.Requests).HasSingleItem();
    await Assert.That(replacement.Requests).HasSingleItem();
    await Assert.That(service.Ensures).IsEqualTo(1);
    await Assert.That(under.Logs.WithEventId(57)).HasSingleItem();
  }

  [Test]
  public async Task A_conversion_ensures_at_most_once()
  {
    await using var deleted = await FakeRenderer.StartAsync(NotFoundAsync);
    await using var service = await FakeProvisioningService.StartAsync(tenant =>
      Record(tenant, deleted)
    );
    // The service still finds the record's sandbox, so the record stays.
    service.Store.Records[Tenant] = Record(Tenant, deleted);
    await using var under = await StartAsync(service);

    var problem = await ReadProblemAsync(await under.ConvertAsync(Tenant));

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer is unavailable.");
    await Assert.That(service.Ensures).IsEqualTo(1);
    await Assert.That(deleted.Requests.Count).IsEqualTo(2);
    await Assert.That(under.Logs.WithEventId(57).Count).IsEqualTo(2);
    await Assert.That(under.Logs.WithEventId(65).Single()["Outcome"]).IsEqualTo("found");
  }

  [Test]
  public async Task A_listed_tenants_renderer_that_the_platform_no_longer_finds_is_not_replaced()
  {
    await using var deleted = await FakeRenderer.StartAsync(NotFoundAsync);
    await using var service = await FakeProvisioningService.StartAsync(tenant =>
      Record(tenant, deleted)
    );
    service.Store.Records["acme"] = Record("acme", deleted);
    await using var under = await StartAsync(service);

    var problem = await ReadProblemAsync(await under.ConvertAsync("acme"));

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(service.Calls).IsEmpty();
    await Assert.That(deleted.Requests).HasSingleItem();
  }

  [Test]
  public async Task Without_on_demand_mode_nothing_is_created_and_no_tenant_can_be_deleted()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never created.")
    );
    await using var under = await StartAsync(
      service,
      ["--ReportsServer:Gateway:Provisioning:Mode=None"]
    );

    var problem = await ReadProblemAsync(await under.ConvertAsync(Tenant));
    using var delete = await under.DeleteAsync(Tenant);

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Detail).IsEqualTo("The tenant has no renderer.");
    await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    await Assert.That(service.Calls).IsEmpty();
    await Assert.That(under.Gateway.Services.GetService<IProvisioningClient>()).IsNull();
  }

  [Test]
  public async Task The_provisioning_client_follows_no_redirect_and_sends_no_trace_headers()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never created.")
    );
    await using var under = await StartAsync(service);

    var factory = under.Gateway.Services.GetRequiredService<IHttpMessageHandlerFactory>();
    HttpMessageHandler handler = factory.CreateHandler("Atli.Reports.Gateway.Provisioning");
    while (handler is DelegatingHandler delegating)
    {
      handler = delegating.InnerHandler!;
    }

    var primary = (SocketsHttpHandler)handler;
    await Assert.That(primary.AllowAutoRedirect).IsFalse();
    await Assert.That(primary.ActivityHeadersPropagator).IsNull();
    await Assert.That(primary.UseCookies).IsFalse();
  }

  /// <summary>The Sandboxes proxy's answer for a sandbox that no longer exists.</summary>
  private static async Task NotFoundAsync(HttpContext context)
  {
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync("""{"error":"Not found"}""", context.RequestAborted);
  }

  private static int ClosedPort()
  {
    using TcpListener probe = new(IPAddress.Loopback, 0);
    probe.Start();
    return ((IPEndPoint)probe.LocalEndpoint).Port;
  }

  /// <summary>
  /// The system's time, counting the timers due after <paramref name="deadline"/>: one per
  /// conversion when it is the gateway's <c>RendererTimeout</c>, started just before the conversion
  /// asks for its renderer.
  /// </summary>
  private sealed class DeadlineCounter(TimeSpan deadline) : TimeProvider
  {
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public override ITimer CreateTimer(
      TimerCallback callback,
      object? state,
      TimeSpan dueTime,
      TimeSpan period
    )
    {
      if (dueTime == deadline)
      {
        Interlocked.Increment(ref _count);
      }

      return base.CreateTimer(callback, state, dueTime, period);
    }
  }
}
