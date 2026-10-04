using System.Collections.Concurrent;
using System.Text;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Hosting.Renderers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Atli.Reports.Engine.Tests.Server.Gateway.GatewayHost;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>
/// A stand-in for the provisioning service (<c>atli-reports-provisioner serve</c>) on a free
/// loopback port: a bare Kestrel app serving <c>PUT</c> and <c>DELETE /tenants/{tenantId}/renderer</c>
/// against a <see cref="FakeRecordStore"/>, as the real service does against the record vault. It
/// records every call, refuses a wrong API key with <c>401</c>, and runs the test's handlers, which
/// by default create a record (<see cref="EnsureAsync"/>) and delete one (<see cref="DeleteAsync"/>).
/// </summary>
internal sealed class FakeProvisioningService : IAsyncDisposable
{
  /// <summary>The gateway's credential for the service.</summary>
  public const string ApiKey = "gateway.provisioning-0123456789abcdefghijklmnopqrstuv";

  /// <summary>The detail of the service's problems, which no caller may see.</summary>
  public const string ServiceWords = "The provisioning service's own words.";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  private readonly WebApplication _app;

  private FakeProvisioningService(
    WebApplication app,
    FakeRecordStore store,
    Func<string, RendererRecord> create
  )
  {
    _app = app;
    Store = store;
    Create = create;
    OnEnsure = EnsureAsync;
    OnDelete = DeleteAsync;
  }

  /// <summary>The service's base address, without a trailing slash.</summary>
  public string BaseUrl => _app.Urls.First();

  /// <summary>The record store the service writes, which the gateway reads.</summary>
  public FakeRecordStore Store { get; }

  /// <summary>The record <see cref="EnsureAsync"/> writes for a tenant without one.</summary>
  public Func<string, RendererRecord> Create { get; set; }

  /// <summary>What <c>PUT</c> does for a tenant; tests may replace it.</summary>
  public Func<HttpContext, string, Task> OnEnsure { get; set; }

  /// <summary>What <c>DELETE</c> does for a tenant; tests may replace it.</summary>
  public Func<HttpContext, string, Task> OnDelete { get; set; }

  /// <summary>Every call, in arrival order.</summary>
  public ConcurrentQueue<ProvisioningCall> Calls { get; } = new();

  public int Ensures => Calls.Count(call => call.Method == HttpMethods.Put);

  public int Deletes => Calls.Count(call => call.Method == HttpMethods.Delete);

  /// <summary>
  /// The gateway settings that point it at this service, with on-demand renderers, and a record
  /// store setting that only passes validation: the test registers <see cref="Store"/>.
  /// </summary>
  public string[] GatewaySettings(string timeout = "00:00:30") =>
    [
      "--ReportsServer:Gateway:Records:Store=File",
      "--ReportsServer:Gateway:Records:Path=/nonexistent/records-unused",
      "--ReportsServer:Gateway:Provisioning:Mode=OnDemand",
      $"--ReportsServer:Gateway:Provisioning:Url={BaseUrl}",
      $"--ReportsServer:Gateway:Provisioning:ApiKey={ApiKey}",
      $"--ReportsServer:Gateway:Provisioning:Timeout={timeout}",
    ];

  /// <summary>Starts the service; <paramref name="create"/> makes a new tenant's record.</summary>
  public static async Task<FakeProvisioningService> StartAsync(
    Func<string, RendererRecord> create,
    FakeRecordStore? store = null
  )
  {
    var builder = WebApplication.CreateSlimBuilder(
      new WebApplicationOptions { Args = ["--urls=http://127.0.0.1:0"] }
    );
    builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
    builder.Logging.ClearProviders();
    var app = builder.Build();
    FakeProvisioningService service = new(app, store ?? new FakeRecordStore(), create);
    app.Map(
      "/tenants/{tenantId}/renderer",
      async (HttpContext context, string tenantId) =>
      {
        service.Calls.Enqueue(
          new ProvisioningCall(
            context.Request.Method,
            tenantId,
            context.Request.Headers.ToDictionary(
              header => header.Key,
              header => header.Value.ToString(),
              StringComparer.OrdinalIgnoreCase
            )
          )
        );
        if (context.Request.Headers["X-Reports-Api-Key"] != ApiKey)
        {
          await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, null);
          return;
        }

        await (
          HttpMethods.IsPut(context.Request.Method) ? service.OnEnsure(context, tenantId)
          : HttpMethods.IsDelete(context.Request.Method) ? service.OnDelete(context, tenantId)
          : WriteProblemAsync(context, StatusCodes.Status405MethodNotAllowed, null)
        );
      }
    );
    await app.StartAsync(TestToken);
    return service;
  }

  /// <summary>
  /// The service's <c>PUT</c>: finds the tenant's record, or writes a new one from
  /// <see cref="Create"/>, and answers as the service does.
  /// </summary>
  public async Task EnsureAsync(HttpContext context, string tenantId)
  {
    var created = false;
    if (!Store.Records.ContainsKey(tenantId))
    {
      Store.Records[tenantId] = Create(tenantId);
      created = true;
    }

    await WriteEnsuredAsync(context, tenantId, created);
  }

  /// <summary>The service's <c>DELETE</c>: removes the tenant's record, if any, and answers <c>204</c>.</summary>
  public Task DeleteAsync(HttpContext context, string tenantId)
  {
    Store.Records.TryRemove(tenantId, out _);
    context.Response.StatusCode = StatusCodes.Status204NoContent;
    return Task.CompletedTask;
  }

  /// <summary>Answers <c>200</c> with the ensure response.</summary>
  public static Task WriteEnsuredAsync(HttpContext context, string tenantId, bool created)
  {
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsync(
      $$"""{"tenantId":"{{tenantId}}","created":{{(created ? "true" : "false")}}}""",
      context.RequestAborted
    );
  }

  /// <summary>
  /// Answers with the service's problem details of <paramref name="kind"/>, whose detail is words
  /// the gateway must never pass on.
  /// </summary>
  public static async Task WriteProblemAsync(
    HttpContext context,
    int status,
    string? kind,
    string? retryAfter = null
  )
  {
    if (retryAfter is not null)
    {
      context.Response.Headers.RetryAfter = retryAfter;
    }

    context.Response.StatusCode = status;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsync(
      kind is null
        ? $$"""{"status":{{status}},"detail":"{{ServiceWords}}"}"""
        : $$"""{"status":{{status}},"kind":"{{kind}}","detail":"{{ServiceWords}}"}""",
      context.RequestAborted
    );
  }

  public async ValueTask DisposeAsync()
  {
    await _app.StopAsync(CancellationToken.None);
    await _app.DisposeAsync();
  }
}

/// <summary>A call the <see cref="FakeProvisioningService"/> received.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="TenantId">The route's tenant.</param>
/// <param name="Headers">The request's headers.</param>
internal sealed record ProvisioningCall(
  string Method,
  string TenantId,
  IReadOnlyDictionary<string, string> Headers
);

/// <summary>
/// A gateway in on-demand mode in front of a <see cref="FakeProvisioningService"/>, whose logs the
/// test can read. The anonymous caller lists the tenant <c>acme</c> and owns the prefix
/// <c>app-</c>; <c>beta-app</c> owns <c>beta-</c>.
/// </summary>
internal sealed class OnDemandGateway : IAsyncDisposable
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  private OnDemandGateway(FakeProvisioningService service, RunningServer gateway, LogCollector logs)
  {
    Service = service;
    Gateway = gateway;
    Logs = logs;
  }

  public FakeProvisioningService Service { get; }

  public RunningServer Gateway { get; }

  public LogCollector Logs { get; }

  public FakeRecordStore Store => Service.Store;

  /// <summary>The anonymous caller's membership: <c>acme</c> listed, the prefix <c>app-</c>.</summary>
  public static string[] Membership() =>
    [
      .. GatewayHost.Membership(0, "anonymous", "acme"),
      "--ReportsServer:Gateway:Tenants:0:TenantPrefixes:0=app-",
      "--ReportsServer:Gateway:Tenants:1:CallerId=beta-app",
      "--ReportsServer:Gateway:Tenants:1:TenantPrefixes:0=beta-",
    ];

  /// <summary>
  /// Starts a gateway; <paramref name="settings"/> come after the defaults, so they can replace the
  /// membership or the service's settings.
  /// </summary>
  public static async Task<OnDemandGateway> StartAsync(
    FakeProvisioningService service,
    string[]? settings = null,
    Action<WebApplicationBuilder>? configure = null
  )
  {
    LogCollector logs = new();
    var gateway = await GatewayHost.StartAsync(
      [.. Membership(), .. service.GatewaySettings(), .. settings ?? []],
      builder =>
      {
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<IRendererRecordStore>(service.Store);
        configure?.Invoke(builder);
      }
    );
    return new OnDemandGateway(service, gateway, logs);
  }

  /// <summary>A record for <paramref name="tenantId"/> that routes to <paramref name="renderer"/>.</summary>
  public static RendererRecord Record(string tenantId, FakeRenderer renderer) =>
    new()
    {
      TenantId = tenantId,
      Url = new Uri(renderer.BaseUrl),
      ApiKey = TestKey,
    };

  /// <summary>Posts a conversion for <paramref name="tenantId"/>, named in the tenant header.</summary>
  public async Task<HttpResponseMessage> ConvertAsync(
    string tenantId,
    CancellationToken? cancellationToken = null
  )
  {
    using HttpRequestMessage request = new(HttpMethod.Post, "/convert")
    {
      Content = new StringContent("""{"html":"<p>x</p>"}""", Encoding.UTF8, "application/json"),
    };
    request.Headers.Add("X-Reports-Tenant", tenantId);
    return await Gateway.Client.SendAsync(request, cancellationToken ?? TestToken);
  }

  /// <summary>
  /// Posts a body that is not a conversion, naming <paramref name="tenantId"/>: the gateway looks up
  /// the tenant's record, as for any conversion, before it reads the body and answers <c>400</c>.
  /// </summary>
  public async Task<HttpResponseMessage> LookUpAsync(string tenantId)
  {
    using HttpRequestMessage request = new(HttpMethod.Post, "/convert")
    {
      Content = new StringContent("not json", Encoding.UTF8, "application/json"),
    };
    request.Headers.Add("X-Reports-Tenant", tenantId);
    return await Gateway.Client.SendAsync(request, TestToken);
  }

  /// <summary>Deletes <paramref name="tenantId"/> through the gateway.</summary>
  public Task<HttpResponseMessage> DeleteAsync(string tenantId) =>
    Gateway.Client.DeleteAsync($"/tenants/{tenantId}", TestToken);

  public async ValueTask DisposeAsync() => await Gateway.DisposeAsync();
}
