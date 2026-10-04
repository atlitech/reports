using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Server;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using static Atli.Reports.Engine.Tests.Server.Gateway.GatewayHost;
using static Atli.Reports.Engine.Tests.Server.Gateway.OnDemandGateway;
using static Atli.Reports.Engine.Tests.Server.SecurityAuthenticationTests;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>
/// <c>DELETE /tenants/{tenantId}</c>: with the permission <c>reports.tenants</c>, a caller deletes
/// the renderer of a tenant under one of its prefixes through the provisioning service, and this
/// replica forgets the tenant's record. Listed tenants, and tenants that are not the caller's, are
/// refused.
/// </summary>
public class GatewayTenantDeletionTests
{
  private const string Tenant = "app-3f2504e0";

  private const string ConverterKey = "converter.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
  private const string ManagerKey = "manager.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

  private const string Issuer = "https://identity.example.test/";
  private const string Audience = "atli-reports";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  /// <summary><c>billing-app</c> lists <c>globex</c> and owns the prefix <c>bill-</c>.</summary>
  private static string[] Billing() =>
    [
      "--ReportsServer:Gateway:Tenants:2:CallerId=billing-app",
      "--ReportsServer:Gateway:Tenants:2:Tenants:0=globex",
      "--ReportsServer:Gateway:Tenants:2:TenantPrefixes:0=bill-",
    ];

  [Test]
  public async Task Deleting_a_prefix_tenant_deletes_its_renderer_and_this_replicas_record()
  {
    await using var first = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7 first")
    );
    await using var second = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7 second")
    );
    await using var service = await FakeProvisioningService.StartAsync(tenant =>
      Record(tenant, second)
    );
    service.Store.Records[Tenant] = Record(Tenant, first);
    await using var under = await StartAsync(service);
    // The record is cached for 30 seconds from here.
    await AssertPdfAsync(await under.ConvertAsync(Tenant), "%PDF-1.7 first");

    using var deleted = await under.DeleteAsync(Tenant);

    await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    var call = service.Calls.Single();
    await Assert.That(call.Method).IsEqualTo("DELETE");
    await Assert.That(call.TenantId).IsEqualTo(Tenant);
    await Assert.That(call.Headers["X-Reports-Api-Key"]).IsEqualTo(FakeProvisioningService.ApiKey);
    await Assert.That(service.Store.Records.ContainsKey(Tenant)).IsFalse();
    var logged = under.Logs.WithEventId(68).Single();
    await Assert.That(logged["CallerId"]).IsEqualTo("anonymous");
    await Assert.That(logged["TenantId"]).IsEqualTo(Tenant);

    // The cached record is gone: the next conversion creates the tenant's renderer anew.
    await AssertPdfAsync(await under.ConvertAsync(Tenant), "%PDF-1.7 second");
    await Assert.That(service.Ensures).IsEqualTo(1);
    await Assert.That(first.Requests).HasSingleItem();
  }

  [Test]
  public async Task A_lookup_that_read_the_store_before_a_deletion_does_not_bring_the_record_back()
  {
    await using var first = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7 first")
    );
    await using var second = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7 second")
    );
    await using var service = await FakeProvisioningService.StartAsync(tenant =>
      Record(tenant, second)
    );
    service.Store.Records[Tenant] = Record(Tenant, first);
    await using var under = await StartAsync(service);
    // A request reads the tenant's record and is held before it answers.
    HeldLookup held = new(service.Store, Tenant);
    var stale = under.LookUpAsync(Tenant);
    await held.Read.WaitAsync(TestToken);

    using var deleted = await under.DeleteAsync(Tenant);
    // The lookup finishes after the deletion, with the record it read before it.
    held.Release();
    using var rejected = await stale;

    await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    // The deleted renderer's record is not cached: the next conversion creates a new renderer.
    await AssertPdfAsync(await under.ConvertAsync(Tenant), "%PDF-1.7 second");
    await Assert.That(service.Ensures).IsEqualTo(1);
    await Assert.That(first.Requests).IsEmpty();
  }

  [Test]
  public async Task Lookups_after_a_deletion_do_not_join_one_that_started_before_it()
  {
    await using var first = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7 first")
    );
    await using var second = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7 second")
    );
    await using var service = await FakeProvisioningService.StartAsync(tenant =>
      Record(tenant, second)
    );
    service.Store.Records[Tenant] = Record(Tenant, first);
    await using var under = await StartAsync(service);
    HeldLookup held = new(service.Store, Tenant);
    var stale = under.LookUpAsync(Tenant);
    await held.Read.WaitAsync(TestToken);

    HttpStatusCode deleted;
    try
    {
      using (var response = await under.DeleteAsync(Tenant))
      {
        deleted = response.StatusCode;
      }

      // While the lookup from before the deletion is still held, a conversion reads the store
      // itself and creates the tenant's new renderer.
      await AssertPdfAsync(
        await under.ConvertAsync(Tenant).WaitAsync(TestEngine.GenerousTimeout, TestToken),
        "%PDF-1.7 second"
      );
    }
    finally
    {
      held.Release();
    }

    using var rejected = await stale;
    await Assert.That(deleted).IsEqualTo(HttpStatusCode.NoContent);
    await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert.That(service.Ensures).IsEqualTo(1);
    await Assert.That(first.Requests).IsEmpty();
  }

  [Test]
  public async Task Deleting_a_tenant_without_a_renderer_succeeds()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never created.")
    );
    await using var under = await StartAsync(service);

    using var deleted = await under.DeleteAsync(Tenant);

    await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    await Assert.That(service.Deletes).IsEqualTo(1);
  }

  [Test]
  public async Task Only_tenants_under_the_callers_prefixes_can_be_deleted()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never created.")
    );
    await using var under = await StartAsync(service, Billing());

    var listed = await ReadProblemAsync(await under.DeleteAsync("acme"));
    await Assert.That(listed.Status).IsEqualTo(403);
    await Assert.That(listed.Kind).IsEqualTo("Forbidden");
    await Assert.That(listed.Detail).Contains("the operator manages its renderer");

    // Another caller's prefix, the prefix alone, another caller's listed tenant, and a stranger.
    foreach (var tenant in new[] { "beta-3f2504e0", "app-", "globex", "initech" })
    {
      var problem = await ReadProblemAsync(await under.DeleteAsync(tenant));
      await Assert.That(problem.Status).IsEqualTo(403);
      await Assert.That(problem.Kind).IsEqualTo("Forbidden");
      await Assert
        .That(problem.Detail)
        .IsEqualTo("The tenant is not under one of the caller's tenant prefixes.");
    }

    foreach (var tenant in new[] { "App-1", "app_1", "-app", new string('a', 64) })
    {
      var problem = await ReadProblemAsync(await under.DeleteAsync(tenant));
      await Assert.That(problem.Status).IsEqualTo(400);
      await Assert.That(problem.Kind).IsEqualTo("InvalidRequest");
      await Assert.That(problem.Title).IsEqualTo("The tenant ID is invalid.");
    }

    await Assert.That(service.Calls).IsEmpty();
    var denied = under.Logs.WithEventId(69);
    await Assert.That(denied.Count).IsEqualTo(5);
    await Assert.That(denied[0]["Reason"]).IsEqualTo("Listed");
    await Assert.That(denied[1]["Reason"]).IsEqualTo("NotMember");
  }

  [Test]
  public async Task Deleting_needs_the_tenants_permission()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never created.")
    );
    await using var under = await StartAsync(
      service,
      [
        "--ReportsServer:Authentication:Mode=ApiKey",
        .. KeySettings(0, "converter", ConverterKey, caller: "billing-app"),
        .. KeySettings(1, "manager", ManagerKey, "reports.tenants", caller: "billing-app"),
        .. Billing(),
      ]
    );

    using var anonymous = await DeleteAsync(under, "bill-3f2504e0", null);
    using var converter = await DeleteAsync(under, "bill-3f2504e0", ConverterKey);
    using var manager = await DeleteAsync(under, "bill-3f2504e0", ManagerKey);
    // Deleting is not converting.
    using var convert = ConvertRequest("""{"html":"<p>x</p>"}""", ManagerKey);
    convert.Headers.Add("X-Reports-Tenant", "bill-3f2504e0");
    using var managerConverts = await under.Gateway.Client.SendAsync(convert, TestToken);

    await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    await Assert.That(converter.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    await Assert.That(manager.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    await Assert.That(managerConverts.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    var call = service.Calls.Single();
    await Assert.That(call.TenantId).IsEqualTo("bill-3f2504e0");
    await Assert.That(call.Headers["X-Reports-Api-Key"]).IsEqualTo(FakeProvisioningService.ApiKey);
    await Assert.That(under.Logs.WithEventId(68).Single()["CallerId"]).IsEqualTo("billing-app");
  }

  [Test]
  [Arguments(null, "reports.tenants", HttpStatusCode.NoContent)]
  [Arguments(null, "reports.convert", HttpStatusCode.Forbidden)]
  [Arguments("billing.tenants", "billing.tenants", HttpStatusCode.NoContent)]
  [Arguments("billing.tenants", "reports.tenants", HttpStatusCode.Forbidden)]
  public async Task Jwt_callers_need_the_configured_tenants_permission(
    string? tenantsPermission,
    string role,
    HttpStatusCode expected
  )
  {
    SymmetricSecurityKey key = new(RandomNumberGenerator.GetBytes(32)) { KeyId = "trusted" };
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never created.")
    );
    await using var under = await StartAsync(
      service,
      [
        .. JwtSettings(),
        .. tenantsPermission is null
          ? Array.Empty<string>()
          : [$"--ReportsServer:Authentication:Jwt:TenantsPermission={tenantsPermission}"],
        .. Billing(),
      ],
      builder => TrustSigningKey(builder, key)
    );
    var token = new JsonWebTokenHandler().CreateToken(
      new SecurityTokenDescriptor
      {
        Issuer = Issuer,
        Audience = Audience,
        Claims = new Dictionary<string, object>
        {
          ["sub"] = "billing-app",
          ["roles"] = new[] { role },
        },
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
      }
    );

    using HttpRequestMessage request = new(HttpMethod.Delete, "/tenants/bill-3f2504e0");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await under.Gateway.Client.SendAsync(request, TestToken);

    await Assert.That(response.StatusCode).IsEqualTo(expected);
    await Assert.That(service.Deletes).IsEqualTo(expected == HttpStatusCode.NoContent ? 1 : 0);
  }

  [Test]
  [Arguments("--ReportsServer:Authentication:Jwt:TenantsPermission=", "claim settings")]
  [Arguments(
    "--ReportsServer:Authentication:Jwt:TenantsPermission=reports.convert",
    "TenantsPermission"
  )]
  public async Task An_invalid_jwt_tenants_permission_fails_at_startup(string setting, string named)
  {
    var exception = await Assert
      .That(() => ReportsServerApplication.Create([.. JwtSettings(), setting]))
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains(named);
  }

  [Test]
  public async Task Service_failures_reach_the_caller_as_the_gateways_own_errors()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never created.")
    );
    var answers = new Queue<Func<Microsoft.AspNetCore.Http.HttpContext, Task>>([
      context => FakeProvisioningService.WriteProblemAsync(context, 503, "Failed"),
      context => FakeProvisioningService.WriteProblemAsync(context, 429, "RateLimited", "20"),
      context => FakeProvisioningService.WriteProblemAsync(context, 403, "NotAllowed"),
    ]);
    service.OnDelete = (context, _) => answers.Dequeue()(context);
    await using var under = await StartAsync(service);

    var failed = await ReadProblemAsync(await under.DeleteAsync(Tenant));
    var limited = await ReadProblemAsync(await under.DeleteAsync(Tenant));
    var refused = await ReadProblemAsync(await under.DeleteAsync(Tenant));

    await Assert.That(failed.Status).IsEqualTo(503);
    await Assert.That(failed.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(failed.Detail).IsEqualTo("The tenant's renderer could not be deleted.");
    await Assert.That(failed.Title).IsEqualTo("The tenant's renderer could not be deleted.");
    await Assert.That(failed.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(limited.Status).IsEqualTo(503);
    await Assert.That(limited.Kind).IsEqualTo("Busy");
    await Assert.That(limited.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(20));
    await Assert.That(refused.Status).IsEqualTo(503);
    await Assert.That(refused.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(refused.Detail).IsEqualTo("The tenant's renderer is unavailable.");
    await Assert.That(under.Logs.WithEventId(67).Single()["Operation"]).IsEqualTo("delete");
    await Assert.That(under.Logs.WithEventId(66).Count).IsEqualTo(2);
    await Assert.That(under.Logs.WithEventId(68)).IsEmpty();
  }

  [Test]
  public async Task A_service_that_cannot_be_reached_fails_the_deletion()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never reached.")
    );
    int port;
    using (TcpListener probe = new(IPAddress.Loopback, 0))
    {
      probe.Start();
      port = ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    await using var under = await StartAsync(
      service,
      [$"--ReportsServer:Gateway:Provisioning:Url=http://127.0.0.1:{port}"]
    );

    var problem = await ReadProblemAsync(await under.DeleteAsync(Tenant));

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(under.Logs.WithEventId(67)).HasSingleItem();
  }

  [Test]
  public async Task The_conversions_tenant_header_and_admission_play_no_part()
  {
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never created.")
    );
    service.OnEnsure = async (context, _) =>
    {
      await release.Task.WaitAsync(context.RequestAborted);
      await FakeProvisioningService.WriteProblemAsync(context, 503, "Failed");
    };
    await using var under = await StartAsync(
      service,
      ["--ReportsServer:Limits:MaxConcurrentRequestsPerCaller=1"]
    );
    // A conversion holds the caller's only admission slot while its tenant's renderer is created.
    var conversion = under.ConvertAsync("app-held");
    var held = await TestEngine.EventuallyAsync(
      () => Task.FromResult(service.Ensures == 1),
      TestEngine.GenerousTimeout
    );

    using HttpRequestMessage request = new(HttpMethod.Delete, $"/tenants/{Tenant}");
    request.Headers.Add("X-Reports-Tenant", "acme");
    using var response = await under.Gateway.Client.SendAsync(request, TestToken);
    // While another conversion would not be admitted.
    using var busy = await under.ConvertAsync("acme");
    release.SetResult();
    using var converted = await conversion;

    await Assert.That(held).IsTrue();
    await Assert.That(busy.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    await Assert.That(service.Calls.Last().TenantId).IsEqualTo(Tenant);
    await Assert.That(service.Deletes).IsEqualTo(1);
    await Assert.That(converted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
  }

  [Test]
  public async Task The_openapi_document_describes_the_deletion()
  {
    await using var service = await FakeProvisioningService.StartAsync(_ =>
      throw new InvalidOperationException("Never created.")
    );
    await using var under = await StartAsync(service);

    using var response = await under.Gateway.Client.GetAsync("/openapi/v1.json", TestToken);
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestToken));

    var operation = document
      .RootElement.GetProperty("paths")
      .GetProperty("/tenants/{tenantId}")
      .GetProperty("delete");
    await Assert
      .That(operation.GetProperty("summary").GetString())
      .IsEqualTo("Deletes a tenant's renderer.");
    var parameter = operation.GetProperty("parameters").EnumerateArray().Single();
    await Assert.That(parameter.GetProperty("name").GetString()).IsEqualTo("tenantId");
    await Assert.That(parameter.GetProperty("in").GetString()).IsEqualTo("path");
    await Assert
      .That(operation.GetProperty("responses").EnumerateObject().Select(status => status.Name))
      .IsEquivalentTo(["204", "400", "401", "403", "503"]);
  }

  private static async Task<HttpResponseMessage> DeleteAsync(
    OnDemandGateway under,
    string tenantId,
    string? credential
  )
  {
    using HttpRequestMessage request = new(HttpMethod.Delete, $"/tenants/{tenantId}");
    if (credential is not null)
    {
      request.Headers.Add("X-Reports-Api-Key", credential);
    }

    return await under.Gateway.Client.SendAsync(request, TestToken);
  }

  private static async Task AssertPdfAsync(HttpResponseMessage response, string expected)
  {
    using (response)
    {
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
      await Assert.That(await response.Content.ReadAsStringAsync(TestToken)).IsEqualTo(expected);
    }
  }

  /// <summary>A test-only trusted metadata snapshot, as the security tests use.</summary>
  private static void TrustSigningKey(WebApplicationBuilder builder, SecurityKey key) =>
    builder.Services.PostConfigure<JwtBearerOptions>(
      JwtBearerDefaults.AuthenticationScheme,
      options =>
      {
        options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        options.Configuration.SigningKeys.Add(key);
      }
    );
}
