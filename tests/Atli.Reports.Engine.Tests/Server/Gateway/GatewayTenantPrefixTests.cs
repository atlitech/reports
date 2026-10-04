using System.Net;
using System.Text;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Server;
using Microsoft.Extensions.DependencyInjection;
using static Atli.Reports.Engine.Tests.Server.Gateway.GatewayHost;
using static Atli.Reports.Engine.Tests.Server.SecurityAuthenticationTests;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>
/// Tenant prefixes: a caller owns every valid tenant ID under its <c>TenantPrefixes</c>, names one in
/// the tenant header, and reaches that tenant's renderer; startup refuses prefixes that would give a
/// tenant two owners.
/// </summary>
public class GatewayTenantPrefixTests
{
  private const string AlphaKey = "alpha.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
  private const string BetaKey = "beta.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

  private const string Workspace = "myapp-3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string OtherWorkspace = "myapp-9b2d6c1a-0d5e-4f3b-8a7c-2e1f0a9b8c7d";

  private const string Url = "https://acme.renderers.example.test/";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task A_tenant_under_the_callers_prefix_reaches_its_renderer()
  {
    await using var renderer = await StartEchoRendererAsync();
    await using var gateway = await GatewayHost.StartAsync([
      .. Membership(0, "anonymous"),
      .. Prefixes(0, "myapp-"),
      .. Renderer(0, Workspace, renderer.BaseUrl, "key-workspace"),
      .. Renderer(1, OtherWorkspace, renderer.BaseUrl, "key-other-workspace"),
    ]);

    await AssertPdfAsync(await SendAsync(gateway, "", Workspace), "%PDF-1.7 key-workspace");
    await AssertPdfAsync(
      await SendAsync(gateway, "", OtherWorkspace),
      "%PDF-1.7 key-other-workspace"
    );
  }

  [Test]
  public async Task Tenants_the_callers_prefixes_do_not_own_are_refused_before_any_record_lookup()
  {
    await using var renderer = await StartEchoRendererAsync();
    FakeRecordStore store = new();
    foreach (var tenant in new[] { Workspace, "myapp-", "globex", "otherapp-1" })
    {
      store.Records[tenant] = new RendererRecord
      {
        TenantId = tenant,
        Url = new Uri(renderer.BaseUrl),
        ApiKey = $"key-{tenant}",
      };
    }

    await using var gateway = await GatewayHost.StartAsync(
      [
        .. Callers(),
        .. Membership(0, "alpha-app"),
        .. Prefixes(0, "myapp-"),
        .. Membership(1, "beta-app", "globex"),
        .. Prefixes(1, "otherapp-"),
        "--ReportsServer:Gateway:Records:Store=File",
        $"--ReportsServer:Gateway:Records:Path={Path.GetTempPath()}",
      ],
      builder => builder.Services.AddSingleton<IRendererRecordStore>(store)
    );

    string[] refused =
    [
      // Another caller's tenants, listed and under its prefix, each with a record.
      "globex",
      "otherapp-1",
      // The bare prefix names no tenant under it, though it is a valid tenant ID with a record.
      "myapp-",
      // Outside the prefix, though sharing its letters.
      "myapp",
      "myappcorp-1",
      // Under the prefix, but not valid tenant IDs.
      "myapp-Workspace",
      "myapp-work_space",
      "myapp-../acme",
      "myapp-" + new string('a', 58),
    ];
    foreach (var tenant in refused)
    {
      var problem = await ReadProblemAsync(await SendAsync(gateway, AlphaKey, tenant));
      await Assert.That(problem.Status).IsEqualTo(403).Because(tenant);
      await Assert.That(problem.Kind).IsEqualTo("Forbidden");
      await Assert.That(problem.Detail).Contains("requested product tenant");
    }

    await Assert.That(store.Lookups).IsEmpty();
    await Assert.That(renderer.Requests).IsEmpty();
    // The same caller still reaches its own tenant.
    await AssertPdfAsync(
      await SendAsync(gateway, AlphaKey, Workspace),
      $"%PDF-1.7 key-{Workspace}"
    );
  }

  [Test]
  public async Task A_caller_with_a_prefix_must_name_its_tenant()
  {
    await using var renderer = await StartEchoRendererAsync();
    await using var gateway = await GatewayHost.StartAsync([
      .. Callers(),
      // A prefix only, and one listed tenant besides a prefix: neither has a tenant to fall back on.
      .. Membership(0, "alpha-app"),
      .. Prefixes(0, "myapp-"),
      .. Membership(1, "beta-app", "acme"),
      .. Prefixes(1, "otherapp-"),
      .. Renderer(0, "acme", renderer.BaseUrl, "key-acme"),
    ]);

    (string Credential, string? Header)[] unnamed =
    [
      (AlphaKey, null),
      (AlphaKey, ""),
      (BetaKey, null),
    ];
    foreach (var (credential, header) in unnamed)
    {
      var problem = await ReadProblemAsync(await SendAsync(gateway, credential, header));
      await Assert.That(problem.Status).IsEqualTo(400);
      await Assert.That(problem.Kind).IsEqualTo("InvalidRequest");
      await Assert.That(problem.Detail).Contains("X-Reports-Tenant");
    }

    await Assert.That(renderer.Requests).IsEmpty();
    await AssertPdfAsync(await SendAsync(gateway, BetaKey, "acme"), "%PDF-1.7 key-acme");
  }

  [Test]
  public async Task Listed_tenants_and_prefixes_combine()
  {
    await using var renderer = await StartEchoRendererAsync();
    await using var gateway = await GatewayHost.StartAsync([
      .. Callers(),
      .. Membership(0, "alpha-app", "acme", "legacy"),
      .. Prefixes(0, "myapp-", "reports-"),
      .. Membership(1, "beta-app", "acme"),
      .. Renderer(0, "acme", renderer.BaseUrl, "key-acme"),
      .. Renderer(1, "legacy", renderer.BaseUrl, "key-legacy"),
      .. Renderer(2, Workspace, renderer.BaseUrl, "key-workspace"),
      .. Renderer(3, "reports-1", renderer.BaseUrl, "key-reports"),
    ]);

    await AssertPdfAsync(await SendAsync(gateway, AlphaKey, "acme"), "%PDF-1.7 key-acme");
    await AssertPdfAsync(await SendAsync(gateway, AlphaKey, "legacy"), "%PDF-1.7 key-legacy");
    await AssertPdfAsync(await SendAsync(gateway, AlphaKey, Workspace), "%PDF-1.7 key-workspace");
    await AssertPdfAsync(await SendAsync(gateway, AlphaKey, "reports-1"), "%PDF-1.7 key-reports");
    // A caller that shares a listed tenant shares none of the other's.
    await AssertPdfAsync(await SendAsync(gateway, BetaKey), "%PDF-1.7 key-acme");
    foreach (var tenant in new[] { "legacy", Workspace, "reports-1" })
    {
      var problem = await ReadProblemAsync(await SendAsync(gateway, BetaKey, tenant));
      await Assert.That(problem.Status).IsEqualTo(403).Because(tenant);
    }
  }

  [Test]
  public async Task Prefix_settings_that_give_each_tenant_one_owner_build_a_gateway()
  {
    await using var app = ReportsServerApplication.Create([
      .. Settings(),
      // Overlapping prefixes of one caller.
      .. Membership(0, "anonymous", "legacy"),
      .. Prefixes(0, "myapp-", "myapp-eu-"),
      // Tenants that only share a prefix's letters, and a listed tenant two callers share.
      .. Membership(1, "billing-app", "myapp", "myappcorp-1", "acme"),
      .. Membership(2, "docs-app", "acme"),
      .. Prefixes(2, "docs-"),
      // A prefix and no listed tenant.
      .. Membership(3, "wiki-app"),
      .. Prefixes(3, "wiki-"),
    ]);

    await Assert.That(app.Services.GetRequiredService<IRendererRecordStore>()).IsNotNull();
  }

  [Test]
  public async Task A_caller_needs_a_tenant_or_a_prefix()
  {
    var exception = await Assert
      .That(() => ReportsServerApplication.Create([.. Settings(), .. Membership(0, "anonymous")]))
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains("'anonymous'");
    await Assert.That(exception.Message).Contains("TenantPrefixes");
  }

  [Test]
  [Arguments("Myapp-")]
  [Arguments("myapp")]
  [Arguments("-myapp-")]
  [Arguments("-")]
  [Arguments("my_app-")]
  [Arguments("myapp-\n")]
  [Arguments("abcdefghijklmnopqrstuvwxyz0-")]
  public async Task An_invalid_prefix_fails_at_startup(string prefix)
  {
    var exception = await Assert
      .That(() =>
        ReportsServerApplication.Create([
          .. Settings(),
          .. Membership(0, "anonymous", "acme"),
          .. Prefixes(0, prefix),
        ])
      )
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains("'anonymous'");
    await Assert.That(exception.Message).Contains("TenantPrefixes");
  }

  [Test]
  public async Task A_callers_prefixes_must_be_distinct()
  {
    var exception = await Assert
      .That(() =>
        ReportsServerApplication.Create([
          .. Settings(),
          .. Membership(0, "anonymous"),
          .. Prefixes(0, "myapp-", "myapp-"),
        ])
      )
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains("'anonymous'");
    await Assert.That(exception.Message).Contains("distinct TenantPrefixes");
  }

  [Test]
  [Arguments("myapp-", "myapp-")]
  [Arguments("myapp-", "myapp-eu-")]
  [Arguments("myapp-eu-", "myapp-")]
  public async Task Prefixes_of_different_callers_must_not_overlap(string first, string second)
  {
    var exception = await Assert
      .That(() =>
        ReportsServerApplication.Create([
          .. Settings(),
          .. Membership(0, "anonymous", "acme"),
          .. Membership(1, "billing-app"),
          .. Prefixes(1, first),
          .. Membership(2, "docs-app"),
          .. Prefixes(2, second),
        ])
      )
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains("overlap");
    await Assert.That(exception.Message).Contains("'billing-app'");
    await Assert.That(exception.Message).Contains("'docs-app'");
  }

  [Test]
  [Arguments(0, 1)]
  [Arguments(1, 0)]
  public async Task A_listed_tenant_may_not_fall_under_another_callers_prefix(int owner, int lister)
  {
    var exception = await Assert
      .That(() =>
        ReportsServerApplication.Create([
          .. Settings(),
          .. Membership(owner, "billing-app"),
          .. Prefixes(owner, "myapp-"),
          .. Membership(lister, "anonymous", "acme", Workspace),
        ])
      )
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains($"'{Workspace}', which is under");
    await Assert.That(exception.Message).Contains("'billing-app'");
    await Assert.That(exception.Message).Contains("'anonymous'");
  }

  [Test]
  public async Task A_listed_tenant_may_not_fall_under_the_callers_own_prefix()
  {
    // The provisioning service retires and deletes the renderers of every tenant under a prefix,
    // and the gateway never has it create a listed tenant's: such a tenant would lose its renderer
    // for good.
    var exception = await Assert
      .That(() =>
        ReportsServerApplication.Create([
          .. Settings(),
          .. Membership(0, "anonymous", "acme", "myapp-legacy"),
          .. Prefixes(0, "myapp-"),
        ])
      )
      .Throws<InvalidOperationException>();

    await Assert
      .That(exception!.Message)
      .Contains(
        "'anonymous' lists the tenant ID 'myapp-legacy', which is under its own tenant prefix 'myapp-'"
      );
    await Assert.That(exception.Message).Contains("no caller may list one");
  }

  [Test]
  public async Task A_prefix_may_not_own_the_readiness_probe_tenant()
  {
    var exception = await Assert
      .That(() =>
        ReportsServerApplication.Create([
          .. Settings(),
          .. Membership(0, "anonymous", "acme"),
          .. Prefixes(0, "readiness-"),
        ])
      )
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains("readiness-probe");
    await Assert.That(exception.Message).Contains("reserves");
  }

  /// <summary>Gateway settings without tenant membership, which each test adds.</summary>
  private static string[] Settings() =>
    [
      "--ReportsServer:Mode=Gateway",
      "--ReportsServer:Authentication:Mode=None",
      "--ReportsServer:Gateway:AllowAnonymousCallers=true",
      .. Renderer(0, "acme", Url, TestKey),
    ];

  /// <summary>Two API-key callers, <c>alpha-app</c> and <c>beta-app</c>, for tests to give tenants.</summary>
  private static string[] Callers() =>
    [
      "--ReportsServer:Authentication:Mode=ApiKey",
      .. KeySettings(0, "alpha", AlphaKey, caller: "alpha-app"),
      .. KeySettings(1, "beta", BetaKey, caller: "beta-app"),
    ];

  private static string[] Prefixes(int index, params string[] prefixes) =>
    [
      .. prefixes.Select(
        (prefix, position) =>
          $"--ReportsServer:Gateway:Tenants:{index}:TenantPrefixes:{position}={prefix}"
      ),
    ];

  /// <summary>
  /// A renderer that answers with a PDF naming the credential it was sent, which tells which
  /// tenant's record the gateway used.
  /// </summary>
  private static Task<FakeRenderer> StartEchoRendererAsync() =>
    FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(
        context,
        $"%PDF-1.7 {context.Request.Headers["X-Reports-Api-Key"]}"
      )
    );

  /// <summary>
  /// Sends a conversion with <paramref name="credential"/> (none when empty) and, unless
  /// <see langword="null"/>, <paramref name="tenant"/> in the tenant header.
  /// </summary>
  private static async Task<HttpResponseMessage> SendAsync(
    RunningServer gateway,
    string credential,
    string? tenant = null
  )
  {
    using var request = ConvertRequest("""{"html":"<p>x</p>"}""", credential);
    if (tenant is not null)
    {
      request.Headers.Add("X-Reports-Tenant", tenant);
    }

    return await gateway.Client.SendAsync(request, TestToken);
  }

  private static async Task AssertPdfAsync(HttpResponseMessage response, string expected)
  {
    using (response)
    {
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
      await Assert
        .That(Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync(TestToken)))
        .IsEqualTo(expected);
    }
  }
}
