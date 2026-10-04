using System.Diagnostics;
using System.Net;
using Atli.Reports.Engine;
using Atli.Reports.Hosting;
using Atli.Reports.Hosting.Provisioning;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Server.Endpoints;
using Atli.Reports.Server.Security;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Gateway mode: <c>/convert</c> keeps its contract, but each conversion goes to the caller's
/// product tenant's own renderer instead of a local engine.
/// </summary>
internal static partial class GatewayRegistration
{
  /// <summary>The trace tag that names the conversion's tenant on the request span.</summary>
  public const string TenantTag = "atli.reports.tenant";

  /// <summary>
  /// Binds and validates <c>ReportsServer:Gateway</c> and registers the gateway in place of the
  /// engine. Runs after the application's <c>configure</c> callback, so test doubles registered
  /// there (a record store, a Sandboxes client) take precedence.
  /// </summary>
  public static void AddReportsGateway(this WebApplicationBuilder builder)
  {
    var settings = new GatewayOptions();
    builder.Configuration.GetSection(GatewayOptions.SectionName).Bind(settings);
    settings.Validate();
    // Under Authentication:Mode=None every caller is "anonymous", so whoever reaches the gateway
    // converts as that caller's tenants.
    if (
      builder.Configuration[GatewayOptions.AuthenticationModeKey] == "None"
      && !settings.AllowAnonymousCallers
    )
    {
      throw new InvalidOperationException(
        "Gateway mode needs authenticated callers: set ReportsServer:Authentication:Mode to ApiKey or JwtBearer. "
          + "For development only, ReportsServer:Gateway:AllowAnonymousCallers=true admits every caller as 'anonymous'."
      );
    }

    var services = builder.Services;
    services.AddSingleton(settings);
    services.TryAddSingleton(TimeProvider.System);
    services.AddHttpContextAccessor();
    services.AddSingleton<TenantMembership>();

    services.TryAddSingleton<IRendererRecordStore>(_ =>
      settings.Records.Store == GatewayRecordsOptions.ConfigurationStore
        ? new ConfigurationRendererRecordStore(settings)
        : RendererRecordStores.Create(settings.Records.ToStoreOptions())
    );
    services.AddSingleton<RendererDirectory>();

    services
      .AddHttpClient(RendererGateway.HttpClientName)
      .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler
        {
          // The credential header must never follow a redirect to another host.
          AllowAutoRedirect = false,
          UseCookies = false,
          AutomaticDecompression = DecompressionMethods.None,
          ConnectTimeout = TimeSpan.FromSeconds(10),
          // No trace context or baggage: ASP.NET Core takes both from the caller's request, and
          // nothing of the caller's request reaches a renderer.
          ActivityHeadersPropagator = null,
        }
      )
      // RendererTimeout is the deadline, including the PDF's body, which HttpClient.Timeout misses.
      .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
      // The gateway logs its own outcomes with tenant and sandbox IDs.
      .RemoveAllLoggers();

    if (settings.Wake.Enabled)
    {
      services
        .AddHttpClient(SandboxWaker.HttpClientName)
        .ConfigurePrimaryHttpMessageHandler(() =>
          new SocketsHttpHandler
          {
            AllowAutoRedirect = false,
            UseCookies = false,
            // The Sandboxes client keeps this HttpClient for the process's lifetime; recycling
            // connections keeps it following DNS changes.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // No trace context or baggage reaches the Azure data plane: a resume serves every
            // request waiting for that sandbox, and none of their callers' context belongs there.
            ActivityHeadersPropagator = null,
          }
        )
        .RemoveAllLoggers();
      services.TryAddSingleton<ISandboxesClient>(provider => new SandboxesClient(
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(SandboxWaker.HttpClientName),
        AzureCredentials.Create(
          string.IsNullOrWhiteSpace(settings.Wake.Sandboxes.ManagedIdentityClientId)
            ? null
            : settings.Wake.Sandboxes.ManagedIdentityClientId
        ),
        settings.Wake.Sandboxes
      ));
      services.AddSingleton<SandboxWaker>();
    }

    if (settings.Provisioning.Enabled)
    {
      services
        .AddHttpClient(TenantProvisioning.HttpClientName)
        // No redirects (the credential header must not follow one), no trace context or baggage,
        // and connections recycled, since the client keeps this HttpClient for the process's life.
        .ConfigurePrimaryHttpMessageHandler(ProvisioningClient.CreateHandler)
        // Provisioning:Timeout bounds each call.
        .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
        .RemoveAllLoggers();
      services.TryAddSingleton<IProvisioningClient>(provider => new ProvisioningClient(
        provider
          .GetRequiredService<IHttpClientFactory>()
          .CreateClient(TenantProvisioning.HttpClientName),
        settings.Provisioning.Url!,
        settings.Provisioning.ApiKey
      ));
      services.AddSingleton<TenantProvisioning>();
    }

    services.AddSingleton(provider => new RendererGateway(
      provider.GetRequiredService<IHttpContextAccessor>(),
      provider.GetRequiredService<RendererDirectory>(),
      provider.GetRequiredService<IHttpClientFactory>(),
      settings,
      provider.GetRequiredService<TimeProvider>(),
      provider.GetRequiredService<ILogger<RendererGateway>>(),
      provider.GetService<SandboxWaker>(),
      provider.GetService<TenantProvisioning>()
    ));
    // Not TryAdd: in gateway mode nothing converts in this process.
    services.AddSingleton<IHtmlToPdfConverter>(provider =>
      provider.GetRequiredService<RendererGateway>()
    );

    services.AddSingleton<RendererRecordsHealthCheck>();
    services
      .AddHealthChecks()
      .Add(
        new HealthCheckRegistration(
          RendererRecordsHealthCheck.Name,
          provider => provider.GetRequiredService<RendererRecordsHealthCheck>(),
          failureStatus: null,
          tags: ["ready"]
        )
      );

    // Callers name their tenant in this header, unless they have only one; the document says so.
    services.Configure<OpenApiOptions>(
      "v1",
      options =>
        options.AddOperationTransformer(
          (operation, context, _) =>
          {
            if (
              context.Description.ActionDescriptor.EndpointMetadata.Any(m =>
                m is ReportsSecurityMiddleware.ConversionAdmissionMetadata
              )
            )
            {
              operation.Parameters ??= [];
              operation.Parameters.Add(
                new OpenApiParameter
                {
                  Name = settings.TenantHeader,
                  In = ParameterLocation.Header,
                  Required = false,
                  Description =
                    "The product tenant to convert for: one the caller lists, or one under one of its tenant prefixes. A caller with one listed tenant and no prefix may omit it.",
                  Schema = new OpenApiSchema
                  {
                    Type = JsonSchemaType.String,
                    Pattern = "^[a-z0-9][a-z0-9-]{0,62}$",
                  },
                }
              );
            }

            return Task.CompletedTask;
          }
        )
    );
  }

  /// <summary>
  /// Resolves each conversion's tenant from the authenticated caller, before the body is read, keeps
  /// the caller's lookups of tenants new to the gateway within its budget, and holds a per-tenant
  /// admission lease for the rest of the request. Runs after caller admission, so the caller's
  /// limits and deadline already apply. With <c>Provisioning:Mode=OnDemand</c>, also maps
  /// <c>DELETE /tenants/{tenantId}</c> (see <see cref="TenantEndpoints"/>).
  /// </summary>
  public static void UseReportsGateway(this WebApplication app)
  {
    // Built now rather than on the first request, so a store, Sandboxes client, or provisioning
    // client that cannot be created fails the start.
    _ = app.Services.GetRequiredService<IRendererRecordStore>();
    _ = app.Services.GetService<SandboxWaker>();
    _ = app.Services.GetService<TenantProvisioning>();

    var settings = app.Services.GetRequiredService<GatewayOptions>();
    if (settings.Provisioning.Enabled)
    {
      app.MapTenantEndpoints();
    }

    var membership = app.Services.GetRequiredService<TenantMembership>();
    // Conversions in flight per tenant.
    InFlightAdmission admission = new();
    NewTenantLookups lookups = new(settings, app.Services.GetRequiredService<TimeProvider>());
    var directory = app.Services.GetRequiredService<RendererDirectory>();
    var logger = app
      .Services.GetRequiredService<ILoggerFactory>()
      .CreateLogger("Atli.Reports.Server.Gateway");
    app.Use(
      async (context, next) =>
      {
        if (
          context
            .GetEndpoint()
            ?.Metadata.GetMetadata<ReportsSecurityMiddleware.ConversionAdmissionMetadata>()
          is null
        )
        {
          await next(context);
          return;
        }

        // The caller identity the security middleware established; "anonymous" only when
        // authentication is explicitly None and AllowAnonymousCallers admits that.
        var caller =
          context.User.FindFirst(ReportsSecurityRegistration.CallerClaim)?.Value ?? "anonymous";
        var resolution = membership.Resolve(caller, context.Request.Headers[settings.TenantHeader]);
        switch (resolution.Rejection)
        {
          case TenantRejection.NoTenants:
          case TenantRejection.NotMember:
            LogTenantDenied(logger, caller, resolution.Rejection);
            // The shape of the authorization 403: status code pages' title, the Forbidden kind.
            await Results
              .Problem(
                statusCode: StatusCodes.Status403Forbidden,
                detail: resolution.Rejection == TenantRejection.NoTenants
                  ? "The caller does not belong to any product tenant."
                  : "The caller does not belong to the requested product tenant.",
                extensions: new Dictionary<string, object?>
                {
                  ["kind"] = nameof(ConversionErrorKind.Forbidden),
                }
              )
              .ExecuteAsync(context);
            return;
          case TenantRejection.HeaderRequired:
          case TenantRejection.HeaderRepeated:
            LogTenantDenied(logger, caller, resolution.Rejection);
            await ConversionProblems.WriteAsync(
              context,
              new ConversionError(
                ConversionErrorKind.InvalidRequest,
                resolution.Rejection == TenantRejection.HeaderRequired
                  ? $"Name the product tenant in the {settings.TenantHeader} header."
                  : $"Send the {settings.TenantHeader} header once."
              )
            );
            return;
        }

        var tenantId = resolution.TenantId!;
        // A caller can name a new tenant under its prefixes on every request, and each costs a read
        // of the record store before the body is read: only within the caller's budget. Answers
        // the directory already has, and tenants it knows had a renderer, cost nothing.
        if (resolution.ViaPrefix && !directory.IsKnown(tenantId) && !lookups.TryTake(caller))
        {
          LogLookupsSpent(logger, caller, lookups.PerSecond);
          await ConversionProblems.WriteAsync(
            context,
            new ConversionError(
              ConversionErrorKind.Busy,
              "The caller named more new tenants than the gateway looks up per second. Retry later."
            )
          );
          return;
        }

        var (limit, feature) = await TenantLimitAsync(
          directory,
          settings,
          tenantId,
          context.RequestAborted
        );
        if (!admission.TryAcquire(tenantId, limit, out var lease))
        {
          LogTenantBusy(logger, tenantId, limit);
          await ConversionProblems.WriteAsync(
            context,
            new ConversionError(
              ConversionErrorKind.Busy,
              "The tenant's in-flight conversion limit was reached. Retry later."
            )
          );
          return;
        }

        using (lease)
        {
          context.Features.Set(feature with { ViaPrefix = resolution.ViaPrefix });
          Activity.Current?.SetTag(TenantTag, tenantId);
          await next(context);
        }
      }
    );
  }

  /// <summary>
  /// The tenant's in-flight limit: the gateway's, or this replica's share of the requests the
  /// tenant's renderer admits (its record's <see cref="RendererRecord.MaxConcurrentRequests"/>)
  /// when that is lower, so a burst waits here as <c>Busy</c> rather than piling onto a renderer
  /// that would refuse it. The record comes from the directory's cache; when it cannot be read, the
  /// gateway's limit applies and the conversion reports the failure.
  /// </summary>
  private static async Task<(int Limit, GatewayTenantFeature Feature)> TenantLimitAsync(
    RendererDirectory directory,
    GatewayOptions settings,
    string tenantId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var record = await directory.GetAsync(tenantId, cancellationToken);
      return (
        settings.TenantLimit(record?.MaxConcurrentRequests),
        new GatewayTenantFeature(tenantId, record, RecordLoaded: true)
      );
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      return (settings.MaxConcurrentRequestsPerTenant, new GatewayTenantFeature(tenantId));
    }
  }

  [LoggerMessage(
    EventId = 52,
    Level = LogLevel.Information,
    Message = "Caller {CallerId} was refused a tenant ({Rejection})."
  )]
  private static partial void LogTenantDenied(
    ILogger logger,
    string callerId,
    TenantRejection rejection
  );

  [LoggerMessage(
    EventId = 53,
    Level = LogLevel.Information,
    Message = "Tenant {TenantId} reached its limit of {Limit} conversions in flight."
  )]
  private static partial void LogTenantBusy(ILogger logger, string tenantId, int limit);

  [LoggerMessage(
    EventId = 70,
    Level = LogLevel.Information,
    Message = "Caller {CallerId} spent its budget of {PerSecond} lookups of new tenants per second."
  )]
  private static partial void LogLookupsSpent(ILogger logger, string callerId, int perSecond);
}

/// <summary>
/// Readiness in gateway mode: the renderer record store answers. The configuration store always
/// does; any other must have answered a lookup within the last <see cref="SuccessLifetime"/>, so
/// probes do not call a remote store each time.
/// </summary>
/// <remarks>
/// The lookup is for <see cref="GatewayOptions.ReadinessProbeTenantId"/>, which no caller can be a
/// member of: one secret or file read, whatever the store holds, and no tenant's record is decoded.
/// Whether it finds nothing or something it cannot read, the store answered; only a store that fails
/// (unreachable, refusing the gateway's identity, timing out) makes the gateway unready. A damaged
/// record of a real tenant fails only that tenant's conversions.
/// </remarks>
internal sealed class RendererRecordsHealthCheck(
  IRendererRecordStore store,
  TimeProvider timeProvider
) : IHealthCheck, IDisposable
{
  public const string Name = "renderer_records";

  private static readonly TimeSpan SuccessLifetime = TimeSpan.FromSeconds(30);

  private static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(10);

  private readonly SemaphoreSlim _gate = new(1, 1);
  private long? _succeededAt;

  public async Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default
  )
  {
    if (store is ConfigurationRendererRecordStore)
    {
      return HealthCheckResult.Healthy("The renderers are listed in configuration.");
    }

    await _gate.WaitAsync(cancellationToken);
    try
    {
      if (_succeededAt is { } at && timeProvider.GetElapsedTime(at) < SuccessLifetime)
      {
        return Healthy();
      }

      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(StoreTimeout);
      try
      {
        _ = await store.GetAsync(GatewayOptions.ReadinessProbeTenantId, timeout.Token);
      }
      catch (InvalidDataException)
      {
        // Something unreadable under the reserved name: the store answered, and nobody routes to it.
      }
      catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
      {
        return HealthCheckResult.Unhealthy(
          $"The renderer record store did not answer ({exception.GetType().Name})."
        );
      }

      _succeededAt = timeProvider.GetTimestamp();
      return Healthy();
    }
    finally
    {
      _gate.Release();
    }
  }

  public void Dispose() => _gate.Dispose();

  private static HealthCheckResult Healthy() =>
    HealthCheckResult.Healthy("The renderer record store answered.");
}
