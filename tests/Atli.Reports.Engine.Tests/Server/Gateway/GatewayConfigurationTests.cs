using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Atli.Reports.Engine.Tests.Server.Gateway.GatewayHost;
using static Atli.Reports.Engine.Tests.Server.SecurityAuthenticationTests;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>
/// Gateway mode's settings, renderer records, readiness, and per-tenant admission.
/// </summary>
public class GatewayConfigurationTests
{
  private const string Url = "https://acme.renderers.example.test/";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  private static string[] Valid() =>
    [
      "--ReportsServer:Mode=Gateway",
      "--ReportsServer:Authentication:Mode=None",
      "--ReportsServer:Gateway:AllowAnonymousCallers=true",
      .. Membership(0, "anonymous", "acme"),
      .. Renderer(0, "acme", Url, TestKey),
    ];

  [Test]
  public async Task Valid_settings_build_a_gateway()
  {
    await using var app = ReportsServerApplication.Create(Valid());

    await Assert.That(app.Services.GetRequiredService<IRendererRecordStore>()).IsNotNull();
  }

  [Test]
  [Arguments("--ReportsServer:Mode=gateway", "ReportsServer:Mode")]
  [Arguments("--ReportsServer:Mode=Proxy", "ReportsServer:Mode")]
  [Arguments("--ReportsServer:Gateway:Tenants:0:CallerId=", "CallerId")]
  [Arguments("--ReportsServer:Gateway:Tenants:0:Tenants:0=Acme", "tenant IDs")]
  [Arguments("--ReportsServer:Gateway:Tenants:0:Tenants:1=acme", "distinct")]
  [Arguments("--ReportsServer:Gateway:Tenants:1:CallerId=anonymous", "unique")]
  [Arguments("--ReportsServer:Gateway:Tenants:0:Tenants:1=readiness-probe", "reserves")]
  [Arguments("--ReportsServer:Gateway:Records:Renderers:0:TenantId=readiness-probe", "TenantId")]
  [Arguments("--ReportsServer:Gateway:TenantHeader=X Tenant", "TenantHeader")]
  [Arguments("--ReportsServer:Gateway:TenantHeader=X-Reports-Api-Key", "TenantHeader")]
  [Arguments("--ReportsServer:Gateway:Records:Store=", "Records:Store")]
  [Arguments("--ReportsServer:Gateway:Records:Store=Redis", "Records:Store")]
  [Arguments("--ReportsServer:Gateway:Records:Store=File", "Records:Path")]
  [Arguments("--ReportsServer:Gateway:Records:Store=KeyVault", "Records:VaultUri")]
  [Arguments("--ReportsServer:Gateway:Records:CacheDuration=-00:00:01", "CacheDuration")]
  [Arguments("--ReportsServer:Gateway:Records:Renderers:0:TenantId=ACME", "TenantId")]
  [Arguments(
    "--ReportsServer:Gateway:Records:Renderers:0:Url=http://acme.renderers.example.test/",
    "AllowHttpRenderers"
  )]
  [Arguments(
    "--ReportsServer:Gateway:Records:Renderers:0:Url=https://user:secret@acme.example.test/",
    "Url"
  )]
  [Arguments(
    "--ReportsServer:Gateway:Records:Renderers:0:Url=https://acme.example.test/?key=1",
    "Url"
  )]
  [Arguments("--ReportsServer:Gateway:Records:Renderers:0:Url=renderers/acme", "Url")]
  [Arguments("--ReportsServer:Gateway:Records:Renderers:0:ApiKey=", "ApiKey")]
  [Arguments("--ReportsServer:Gateway:Records:Renderers:0:ApiKey=two words", "ApiKey")]
  [Arguments("--ReportsServer:Gateway:Records:Renderers:0:SandboxId=../x", "SandboxId")]
  [Arguments(
    "--ReportsServer:Gateway:Records:Renderers:0:MaxConcurrentRequests=0",
    "MaxConcurrentRequests"
  )]
  [Arguments("--ReportsServer:Gateway:Wake:Mode=Always", "Wake:Mode")]
  [Arguments("--ReportsServer:Gateway:Wake:Mode=Sandboxes", "Wake:Sandboxes")]
  [Arguments("--ReportsServer:Gateway:Wake:Timeout=00:00:00", "Wake:Timeout")]
  [Arguments("--ReportsServer:Gateway:RendererTimeout=00:00:00", "RendererTimeout")]
  [Arguments("--ReportsServer:Gateway:MaxPdfBytes=1023", "MaxPdfBytes")]
  [Arguments(
    "--ReportsServer:Gateway:MaxConcurrentRequestsPerTenant=0",
    "MaxConcurrentRequestsPerTenant"
  )]
  public async Task Invalid_settings_fail_at_startup_naming_the_setting(
    string setting,
    string named
  )
  {
    var exception = await Assert
      .That(() => ReportsServerApplication.Create([.. Valid(), setting]))
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains(named);
  }

  [Test]
  public async Task A_gateway_needs_tenant_membership()
  {
    var exception = await Assert
      .That(() =>
        ReportsServerApplication.Create([
          "--ReportsServer:Mode=Gateway",
          "--ReportsServer:Authentication:Mode=None",
          "--ReportsServer:Gateway:AllowAnonymousCallers=true",
          .. Renderer(0, "acme", Url, TestKey),
        ])
      )
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains("ReportsServer:Gateway:Tenants");
  }

  [Test]
  public async Task Anonymous_callers_need_the_development_flag()
  {
    string[] anonymous =
    [
      "--ReportsServer:Mode=Gateway",
      "--ReportsServer:Authentication:Mode=None",
      .. Membership(0, "anonymous", "acme"),
      .. Renderer(0, "acme", Url, TestKey),
    ];

    var refused = await Assert
      .That(() => ReportsServerApplication.Create(anonymous))
      .Throws<InvalidOperationException>();
    await using var allowed = ReportsServerApplication.Create([
      .. anonymous,
      "--ReportsServer:Gateway:AllowAnonymousCallers=true",
    ]);
    // Authenticated callers need no flag.
    await using var authenticated = ReportsServerApplication.Create([
      "--ReportsServer:Mode=Gateway",
      "--ReportsServer:Authentication:Mode=ApiKey",
      .. KeySettings(
        0,
        "primary",
        "primary.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG",
        caller: "billing-app"
      ),
      .. Membership(0, "billing-app", "acme"),
      .. Renderer(0, "acme", Url, TestKey),
    ]);

    await Assert.That(refused!.Message).Contains("AllowAnonymousCallers");
    await Assert.That(refused.Message).Contains("ReportsServer:Authentication:Mode");
    await Assert.That(allowed.Services.GetRequiredService<IRendererRecordStore>()).IsNotNull();
    await Assert
      .That(authenticated.Services.GetRequiredService<IRendererRecordStore>())
      .IsNotNull();
  }

  [Test]
  public async Task Gateway_settings_do_not_affect_integrated_mode()
  {
    // Integrated is the default; a stray gateway section is ignored rather than validated.
    await using var app = ReportsServerApplication.Create([
      "--ReportsServer:Authentication:Mode=None",
      "--ReportsEngine:Browser:WarmUpOnStartup=false",
      "--ReportsServer:Gateway:MaxPdfBytes=0",
    ]);

    await Assert.That(app.Services.GetService<IRendererRecordStore>()).IsNull();
  }

  [Test]
  public async Task Records_are_cached_and_a_missing_record_briefly_too()
  {
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    FakeRecordStore store = new();
    store.Records["acme"] = Record("acme", renderer.BaseUrl);
    await using var gateway = await StartWithStoreAsync(store, []);

    using (var first = await gateway.PostAsync("""{"html":"<p>x</p>"}"""))
    using (var second = await gateway.PostAsync("""{"html":"<p>x</p>"}"""))
    {
      await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
      await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    await Assert.That(store.Gets).IsEqualTo(1);
  }

  [Test]
  public async Task Without_a_cache_every_conversion_reads_the_store()
  {
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    FakeRecordStore store = new();
    store.Records["acme"] = Record("acme", renderer.BaseUrl);
    await using var gateway = await StartWithStoreAsync(
      store,
      ["--ReportsServer:Gateway:Records:CacheDuration=00:00:00"]
    );

    using (await gateway.PostAsync("""{"html":"<p>x</p>"}""")) { }
    using (await gateway.PostAsync("""{"html":"<p>x</p>"}""")) { }

    await Assert.That(store.Gets).IsEqualTo(2);
  }

  [Test]
  public async Task A_tenant_without_a_record_is_unavailable()
  {
    FakeRecordStore store = new();
    LogCollector logs = new();
    await using var gateway = await StartWithStoreAsync(
      store,
      [],
      builder => builder.Logging.AddProvider(logs)
    );

    var first = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));
    var second = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));

    await Assert.That(first.Status).IsEqualTo(503);
    await Assert.That(first.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(first.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(second.Status).IsEqualTo(503);
    await Assert.That(store.Gets).IsEqualTo(1).Because("a missing record is cached briefly");
    await Assert.That(logs.WithEventId(40).Count).IsEqualTo(2);
    await Assert.That(logs.WithEventId(40)[0]["TenantId"]).IsEqualTo("acme");
  }

  [Test]
  public async Task A_failing_store_is_unavailable_and_not_cached()
  {
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    FakeRecordStore store = new() { Failure = new IOException("The vault is unreachable.") };
    store.Records["acme"] = Record("acme", renderer.BaseUrl);
    LogCollector logs = new();
    await using var gateway = await StartWithStoreAsync(
      store,
      [],
      builder => builder.Logging.AddProvider(logs)
    );

    var failed = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));
    store.Failure = null;
    using var recovered = await gateway.PostAsync("""{"html":"<p>x</p>"}""");

    await Assert.That(failed.Status).IsEqualTo(503);
    await Assert.That(failed.Detail).IsEqualTo("The renderer directory is unavailable.");
    await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(logs.WithEventId(41)).HasSingleItem();
  }

  [Test]
  [Arguments("another tenant")]
  [Arguments("http")]
  public async Task A_record_the_gateway_cannot_trust_is_not_used(string problem)
  {
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    FakeRecordStore store = new();
    store.Records["acme"] = Record(
      problem == "another tenant" ? "globex" : "acme",
      renderer.BaseUrl
    );
    await using var gateway = await StartWithStoreAsync(
      store,
      problem == "http" ? ["--ReportsServer:Gateway:AllowHttpRenderers=false"] : []
    );

    var response = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));

    await Assert.That(response.Status).IsEqualTo(503);
    await Assert.That(renderer.Requests).IsEmpty();
  }

  [Test]
  public async Task Readiness_follows_the_record_store()
  {
    FakeRecordStore store = new() { Failure = new IOException("The vault is unreachable.") };
    await using var gateway = await StartWithStoreAsync(store, []);

    var failing = await GetHealthAsync(gateway, "/health/details");
    store.Failure = null;
    var recovered = await GetHealthAsync(gateway, "/health/ready");
    var again = await GetHealthAsync(gateway, "/health/ready");
    var live = await GetHealthAsync(gateway, "/health/live");

    await Assert.That(failing.Status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    await Assert.That(failing.Body).Contains("renderer_records");
    await Assert.That(failing.Body).DoesNotContain("browser");
    await Assert.That(recovered.Status).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(again.Status).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(store.Gets).IsEqualTo(2).Because("a recent success is reused");
    // One lookup of the reserved tenant, never a listing of every record.
    await Assert.That(store.Lookups).IsEquivalentTo(["readiness-probe", "readiness-probe"]);
    await Assert.That(store.Lists).IsEqualTo(0);
    await Assert.That(live.Status).IsEqualTo(HttpStatusCode.OK);
  }

  [Test]
  public async Task A_damaged_record_fails_only_its_own_tenant()
  {
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    var directory = Directory.CreateTempSubdirectory("gateway-records-");
    try
    {
      await File.WriteAllTextAsync(
        Path.Combine(directory.FullName, "acme.json"),
        $$"""{"tenantId":"acme","url":"{{renderer.BaseUrl}}","apiKey":"{{TestKey}}"}""",
        TestToken
      );
      // Another tenant's record names acme, and the reserved name holds no record at all.
      await File.WriteAllTextAsync(
        Path.Combine(directory.FullName, "globex.json"),
        $$"""{"tenantId":"acme","url":"{{renderer.BaseUrl}}","apiKey":"{{TestKey}}"}""",
        TestToken
      );
      await File.WriteAllTextAsync(
        Path.Combine(directory.FullName, "readiness-probe.json"),
        "not a record",
        TestToken
      );
      await using var gateway = await GatewayHost.StartAsync([
        .. Membership(0, "anonymous", "acme", "globex"),
        "--ReportsServer:Gateway:Records:Store=File",
        $"--ReportsServer:Gateway:Records:Path={directory.FullName}",
      ]);

      var ready = await GetHealthAsync(gateway, "/health/ready");
      using var acme = await SendAsync(gateway, "acme");
      var globex = await ReadProblemAsync(await SendAsync(gateway, "globex"));

      await Assert.That(ready.Status).IsEqualTo(HttpStatusCode.OK);
      await Assert.That(acme.StatusCode).IsEqualTo(HttpStatusCode.OK);
      await Assert.That(globex.Status).IsEqualTo(503);
      await Assert.That(globex.Kind).IsEqualTo("BrowserUnavailable");
      await Assert.That(renderer.Requests.Count).IsEqualTo(1);
    }
    finally
    {
      directory.Delete(recursive: true);
    }
  }

  [Test]
  public async Task A_record_whose_renderer_cannot_be_reached_is_read_again()
  {
    int port;
    using (System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0))
    {
      probe.Start();
      port = ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    FakeRecordStore store = new();
    store.Records["acme"] = Record("acme", $"http://127.0.0.1:{port}");
    await using var gateway = await StartWithStoreAsync(store, []);

    var unreachable = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));
    // A rollout recreated the renderer elsewhere; the record cache must not keep the old one.
    store.Records["acme"] = Record("acme", renderer.BaseUrl);
    using var moved = await gateway.PostAsync("""{"html":"<p>x</p>"}""");

    await Assert.That(unreachable.Status).IsEqualTo(503);
    await Assert.That(moved.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(store.Gets).IsEqualTo(2);
  }

  [Test]
  public async Task A_record_whose_credential_is_rejected_is_read_again()
  {
    await using var renderer = await FakeRenderer.StartAsync(context =>
      context.Request.Headers["X-Reports-Api-Key"] == "reports-000000000000.rotated"
        ? FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
        : FakeRenderer.WriteProblemAsync(
          context,
          StatusCodes.Status401Unauthorized,
          """{"kind":"Unauthorized"}"""
        )
    );
    FakeRecordStore store = new();
    store.Records["acme"] = Record("acme", renderer.BaseUrl);
    await using var gateway = await StartWithStoreAsync(store, []);

    var rejected = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));
    // The provisioner rotated the renderer's credential and wrote the new record.
    store.Records["acme"] = Record("acme", renderer.BaseUrl) with
    {
      ApiKey = "reports-000000000000.rotated",
    };
    using var rotated = await gateway.PostAsync("""{"html":"<p>x</p>"}""");

    await Assert.That(rejected.Status).IsEqualTo(503);
    await Assert.That(rotated.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(store.Gets).IsEqualTo(2);
  }

  [Test]
  public async Task A_record_whose_renderer_the_platform_no_longer_finds_is_read_again()
  {
    // The Sandboxes proxy's answer for a deleted sandbox, observed on 2026-10-04.
    await using var deleted = await FakeRenderer.StartAsync(async context =>
    {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      context.Response.ContentType = "application/json; charset=utf-8";
      await context.Response.WriteAsync("""{"error":"Not found"}""", context.RequestAborted);
    });
    await using var replacement = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    FakeRecordStore store = new();
    store.Records["acme"] = Record("acme", deleted.BaseUrl);
    LogCollector logs = new();
    await using var gateway = await StartWithStoreAsync(
      store,
      [],
      builder => builder.Logging.AddProvider(logs)
    );

    var gone = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));
    // The provisioner recreated the renderer and wrote its record; the cached one must not stay.
    store.Records["acme"] = Record("acme", replacement.BaseUrl);
    using var moved = await gateway.PostAsync("""{"html":"<p>x</p>"}""");

    await Assert.That(gone.Status).IsEqualTo(503);
    await Assert.That(gone.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(moved.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(store.Gets).IsEqualTo(2);
    var notFound = logs.WithEventId(57).Single();
    await Assert.That(notFound.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(notFound["TenantId"]).IsEqualTo("acme");
  }

  [Test]
  public async Task A_port_that_refuses_the_gateways_address_is_the_gateways_error()
  {
    await using var renderer = await FakeRenderer.StartAsync(async context =>
    {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      context.Response.ContentType = "application/json; charset=utf-8";
      await context.Response.WriteAsync(
        """{"error":"Access denied by IP access control policy","errorCode":"IpAccessDenied"}""",
        context.RequestAborted
      );
    });
    FakeRecordStore store = new();
    store.Records["acme"] = Record("acme", renderer.BaseUrl);
    LogCollector logs = new();
    await using var gateway = await StartWithStoreAsync(
      store,
      [],
      builder => builder.Logging.AddProvider(logs)
    );

    var first = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));
    var second = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));

    // Not the document's failure (500 RenderFailed): the renderer is unreachable for the gateway.
    await Assert.That(first.Status).IsEqualTo(503);
    await Assert.That(first.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(second.Status).IsEqualTo(503);
    // The record is right; only the port's allow-list is not, so the record stays cached.
    await Assert.That(store.Gets).IsEqualTo(1);
    var denied = logs.WithEventId(58);
    await Assert.That(denied.Count).IsEqualTo(2);
    await Assert.That(denied.All(entry => entry.Level == LogLevel.Error)).IsTrue();
    await Assert.That(logs.WithEventId(45)).IsEmpty();
  }

  [Test]
  public async Task Shared_lookups_carry_none_of_the_callers_trace_context()
  {
    ConcurrentQueue<Activity> requests = new();
    using ActivityListener listener = new()
    {
      ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
      ActivityStarted = requests.Enqueue,
    };
    ActivitySource.AddActivityListener(listener);
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    FakeRecordStore store = new();
    store.Records["acme"] = Record("acme", renderer.BaseUrl);
    await using var gateway = await StartWithStoreAsync(store, []);

    using HttpRequestMessage request = new(HttpMethod.Post, "/convert")
    {
      Content = new StringContent("""{"html":"<p>x</p>"}""", Encoding.UTF8, "application/json"),
    };
    request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
    request.Headers.Add("baggage", "secret=caller-baggage");
    using var response = await gateway.Client.SendAsync(request, TestToken);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    // The request ran under the caller's trace...
    await Assert
      .That(
        requests.Any(activity =>
          activity.TraceId.ToHexString() == "0af7651916cd43dd8448eb211c80319c"
        )
      )
      .IsTrue();
    // ...and the record lookup it started did not.
    await Assert.That(store.Activities).HasSingleItem();
    await Assert.That(store.Activities.Single()).IsNull();
  }

  [Test]
  public async Task The_configuration_store_is_always_ready()
  {
    await using var under = await GatewayUnderTest.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );

    var ready = await GetHealthAsync(under.Gateway, "/health/ready");

    await Assert.That(ready.Status).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(ready.Body).IsEqualTo("""{"status":"Healthy"}""");
  }

  [Test]
  public async Task A_tenant_at_its_limit_is_busy_across_callers()
  {
    const string firstKey = "first.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
    const string secondKey = "second.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
    const string otherKey = "other.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var renderer = await FakeRenderer.StartAsync(async context =>
    {
      if (context.Request.Headers["X-Reports-Api-Key"] == "reports-000000000000.acme")
      {
        entered.TrySetResult();
        await release.Task.WaitAsync(context.RequestAborted);
      }

      await FakeRenderer.WritePdfAsync(context, "%PDF-1.7");
    });
    await using var gateway = await GatewayHost.StartAsync([
      "--ReportsServer:Authentication:Mode=ApiKey",
      "--ReportsServer:Gateway:MaxConcurrentRequestsPerTenant=1",
      .. KeySettings(0, "first", firstKey, caller: "first-app"),
      .. KeySettings(1, "second", secondKey, caller: "second-app"),
      .. KeySettings(2, "other", otherKey, caller: "other-app"),
      .. Membership(0, "first-app", "acme"),
      .. Membership(1, "second-app", "acme"),
      .. Membership(2, "other-app", "globex"),
      .. Renderer(0, "acme", renderer.BaseUrl, "reports-000000000000.acme"),
      .. Renderer(1, "globex", renderer.BaseUrl, "reports-000000000000.globex"),
    ]);

    using var firstRequest = ConvertRequest(credential: firstKey);
    var first = gateway.Client.SendAsync(firstRequest, TestToken);
    await entered.Task.WaitAsync(TestToken);
    Problem busy;
    HttpStatusCode otherTenant;
    try
    {
      using var secondRequest = ConvertRequest(credential: secondKey);
      busy = await ReadProblemAsync(await gateway.Client.SendAsync(secondRequest, TestToken));
      using var otherRequest = ConvertRequest(credential: otherKey);
      using var other = await gateway.Client.SendAsync(otherRequest, TestToken);
      otherTenant = other.StatusCode;
    }
    finally
    {
      release.TrySetResult();
    }

    using var completed = await first;
    using var nextRequest = ConvertRequest(credential: secondKey);
    using var next = await gateway.Client.SendAsync(nextRequest, TestToken);

    await Assert.That(busy.Status).IsEqualTo(503);
    await Assert.That(busy.Kind).IsEqualTo("Busy");
    await Assert.That(busy.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(otherTenant).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(next.StatusCode)
      .IsEqualTo(HttpStatusCode.OK)
      .Because("the lease was released");
  }

  [Test]
  public async Task The_renderers_admitted_requests_cap_its_tenants_limit()
  {
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var renderer = await FakeRenderer.StartAsync(async context =>
    {
      entered.TrySetResult();
      await release.Task.WaitAsync(context.RequestAborted);
      await FakeRenderer.WritePdfAsync(context, "%PDF-1.7");
    });
    // The gateway allows 8; the record says the renderer admits 1.
    await using var gateway = await GatewayHost.StartAsync([
      .. Membership(0, "anonymous", "acme"),
      .. Renderer(0, "acme", renderer.BaseUrl, TestKey, maxConcurrentRequests: 1),
    ]);

    using var firstRequest = ConvertRequest();
    var first = gateway.Client.SendAsync(firstRequest, TestToken);
    await entered.Task.WaitAsync(TestToken);
    Problem busy;
    try
    {
      using var secondRequest = ConvertRequest();
      busy = await ReadProblemAsync(await gateway.Client.SendAsync(secondRequest, TestToken));
    }
    finally
    {
      release.TrySetResult();
    }

    using var completed = await first;
    await Assert.That(busy.Status).IsEqualTo(503);
    await Assert.That(busy.Kind).IsEqualTo("Busy");
    await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.OK);
  }

  private static RendererRecord Record(string tenantId, string url) =>
    new()
    {
      TenantId = tenantId,
      Url = new Uri(url),
      ApiKey = TestKey,
    };

  /// <summary>
  /// A gateway over <paramref name="store"/>, with the anonymous caller in <c>acme</c>. The File
  /// settings only satisfy validation; the injected store replaces the one they would create.
  /// </summary>
  private static Task<RunningServer> StartWithStoreAsync(
    FakeRecordStore store,
    string[] settings,
    Action<WebApplicationBuilder>? configure = null
  ) =>
    GatewayHost.StartAsync(
      [
        .. Membership(0, "anonymous", "acme"),
        "--ReportsServer:Gateway:Records:Store=File",
        $"--ReportsServer:Gateway:Records:Path={Path.GetTempPath()}",
        .. settings,
      ],
      builder =>
      {
        builder.Services.AddSingleton<IRendererRecordStore>(store);
        configure?.Invoke(builder);
      }
    );

  private static async Task<HttpResponseMessage> SendAsync(RunningServer gateway, string tenant)
  {
    using HttpRequestMessage request = new(HttpMethod.Post, "/convert")
    {
      Content = new StringContent("""{"html":"<p>x</p>"}""", Encoding.UTF8, "application/json"),
    };
    request.Headers.Add("X-Reports-Tenant", tenant);
    return await gateway.Client.SendAsync(request, TestToken);
  }

  private static async Task<(HttpStatusCode Status, string Body)> GetHealthAsync(
    RunningServer server,
    string path
  )
  {
    using var response = await server.Client.GetAsync(path, TestToken);
    var body = await response.Content.ReadAsStringAsync(TestToken);
    using var _ = JsonDocument.Parse(body);
    return (response.StatusCode, body);
  }
}
