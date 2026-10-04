using System.Collections.Concurrent;
using System.Globalization;
using Atli.Reports.Engine;
using Atli.Reports.Hosting.Provisioning;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Server.Endpoints;
using Atli.Reports.Server.Security;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Maps <c>DELETE /tenants/{tenantId}</c>, with which an application deletes the renderer of a tenant
/// under one of its tenant prefixes. Gateway mode maps it only with
/// <c>Provisioning:Mode=OnDemand</c>.
/// </summary>
internal static partial class TenantEndpoints
{
  /// <summary>Maps <c>DELETE /tenants/{tenantId}</c> and describes it to OpenAPI.</summary>
  public static void MapTenantEndpoints(this WebApplication app) =>
    // No ConversionAdmissionMetadata: caller admission, the tenant header, and tenant admission are
    // the conversion's; the tenant here is the route's.
    app.MapDelete("/tenants/{tenantId}", DeleteTenant)
      .WithName("DeleteTenant")
      .RequireAuthorization(ReportsSecurityRegistration.TenantsPolicy)
      .WithTags("Tenants")
      .Produces(StatusCodes.Status204NoContent)
      .ProducesProblem(StatusCodes.Status400BadRequest)
      .ProducesProblem(StatusCodes.Status401Unauthorized)
      .ProducesProblem(StatusCodes.Status403Forbidden)
      .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

  /// <summary>
  /// Deletes a tenant's renderer.
  /// </summary>
  /// <remarks>
  /// Deletes the renderer of a tenant under one of the caller's tenant prefixes, with its record and
  /// memory snapshot; the tenant's next conversion creates a new one. Succeeds when the tenant has no
  /// renderer. Other gateway replicas may still route the tenant's conversions to the deleted
  /// renderer for as long as they cache its record, and such a conversion creates a new renderer, so
  /// stop converting for a tenant before deleting it.
  /// </remarks>
  /// <param name="context">The request.</param>
  /// <param name="tenantId">The tenant: a tenant ID under one of the caller's tenant prefixes.</param>
  /// <param name="membership">The callers' tenants.</param>
  /// <param name="provisioning">The provisioning service.</param>
  /// <param name="loggerFactory">For the deletion's log.</param>
  /// <response code="204">The tenant has no renderer any more.</response>
  /// <response code="400"><c>InvalidRequest</c>: the tenant ID is not one.</response>
  /// <response code="401"><c>Unauthorized</c>: credentials are missing or invalid.</response>
  /// <response code="403">
  /// <c>Forbidden</c>: the caller lacks the tenants permission, or the tenant is not under one of its
  /// tenant prefixes. Listed tenants are managed by the operator.
  /// </response>
  /// <response code="503">
  /// <c>BrowserUnavailable</c>: the renderer could not be deleted now (<c>Retry-After: 5</c>).
  /// <c>Busy</c>: the provisioning service is at its rate limit (<c>Retry-After</c> says how long).
  /// </response>
  internal static async Task DeleteTenant(
    HttpContext context,
    string tenantId,
    TenantMembership membership,
    TenantProvisioning provisioning,
    ILoggerFactory loggerFactory
  )
  {
    if (!TenantId.IsValid(tenantId))
    {
      await WriteProblemAsync(
        context,
        "The tenant ID is invalid.",
        new ConversionError(
          ConversionErrorKind.InvalidRequest,
          "A tenant ID is 1 to 63 lowercase letters, digits, and hyphens, starting with a letter or digit."
        )
      );
      return;
    }

    var logger = loggerFactory.CreateLogger("Atli.Reports.Server.Gateway");
    var caller =
      context.User.FindFirst(ReportsSecurityRegistration.CallerClaim)?.Value ?? "anonymous";
    // The route names the tenant as the header names a conversion's: only a selector among the
    // caller's own tenants.
    var resolution = membership.Resolve(caller, tenantId);
    if (resolution is not { Rejection: TenantRejection.None, ViaPrefix: true })
    {
      var listed = resolution.Rejection == TenantRejection.None;
      LogDeleteDenied(
        logger,
        caller,
        tenantId,
        listed ? "Listed" : resolution.Rejection.ToString()
      );
      // The shape of the gateway's other 403s: status code pages' title, the Forbidden kind.
      await Results
        .Problem(
          statusCode: StatusCodes.Status403Forbidden,
          detail: listed
            ? "The tenant is listed in the gateway's configuration, so the operator manages its renderer. Only tenants under the caller's tenant prefixes can be deleted."
            : "The tenant is not under one of the caller's tenant prefixes.",
          extensions: new Dictionary<string, object?>
          {
            ["kind"] = nameof(ConversionErrorKind.Forbidden),
          }
        )
        .ExecuteAsync(context);
      return;
    }

    if (await provisioning.DeleteAsync(tenantId, context.RequestAborted) is { } failure)
    {
      await WriteProblemAsync(
        context,
        "The tenant's renderer could not be deleted.",
        failure.Error,
        failure.RetryAfterSeconds
      );
      return;
    }

    LogDeleted(logger, caller, tenantId);
    context.Response.StatusCode = StatusCodes.Status204NoContent;
  }

  /// <summary>
  /// Writes <paramref name="error"/> as <see cref="ConversionProblems.WriteAsync"/> does, with its
  /// status, kind, and <c>Retry-After</c> (<paramref name="retryAfterSeconds"/> when given), but
  /// titled for the deletion rather than a conversion.
  /// </summary>
  private static Task WriteProblemAsync(
    HttpContext context,
    string title,
    ConversionError error,
    int? retryAfterSeconds = null
  )
  {
    if ((retryAfterSeconds ?? ConversionProblems.RetryAfterSeconds(error.Kind)) is { } retryAfter)
    {
      context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
    }

    return Results
      .Problem(
        detail: error.Message,
        statusCode: ConversionProblems.StatusCode(error.Kind),
        title: title,
        extensions: new Dictionary<string, object?> { ["kind"] = error.Kind.ToString() }
      )
      .ExecuteAsync(context);
  }

  [LoggerMessage(
    EventId = 68,
    Level = LogLevel.Information,
    Message = "Caller {CallerId} deleted the renderer of tenant {TenantId}."
  )]
  private static partial void LogDeleted(ILogger logger, string callerId, string tenantId);

  [LoggerMessage(
    EventId = 69,
    Level = LogLevel.Information,
    Message = "Caller {CallerId} was refused the deletion of tenant {TenantId} ({Reason})."
  )]
  private static partial void LogDeleteDenied(
    ILogger logger,
    string callerId,
    string tenantId,
    string reason
  );
}

/// <summary>
/// The gateway's side of the provisioning service (<see cref="ProvisioningApi"/>), for tenants under
/// its callers' prefixes: has it create a tenant's renderer when the tenant has none, and delete
/// one for the tenant's caller. The service's own words never reach the caller.
/// </summary>
/// <remarks>
/// Concurrent conversions of a tenant share one ensure call, which runs apart from the request that
/// starts it (see <see cref="SharedWork"/>), within <see cref="GatewayProvisioningOptions.Timeout"/>;
/// each request waits for it only as long as its own deadline allows. Once the service has
/// answered, the call reads the tenant's record into the directory, so the waiting requests find
/// the new renderer.
/// </remarks>
internal sealed partial class TenantProvisioning(
  IProvisioningClient client,
  RendererDirectory directory,
  GatewayOptions options,
  TimeProvider timeProvider,
  ILogger<TenantProvisioning> logger
)
{
  /// <summary>The named <see cref="HttpClient"/> the provisioning client sends with.</summary>
  public const string HttpClientName = "Atli.Reports.Gateway.Provisioning";

  /// <summary>
  /// The caller's message for <see cref="ProvisioningProblemKinds.QuotaExceeded"/>, which lasts until
  /// the application deletes tenants or idle ones retire.
  /// </summary>
  private const string QuotaFull =
    "The renderer quota of the tenant's prefix is full. Delete tenants that are no longer used, or ask the operator to raise the quota.";

  /// <summary>The bounds of a <c>Retry-After</c> the gateway passes on from the service.</summary>
  private const int MinRetryAfterSeconds = 1;

  private const int MaxRetryAfterSeconds = 60;

  private readonly ConcurrentDictionary<string, Lazy<Task<ProvisioningFailure?>>> _ensures = new(
    StringComparer.Ordinal
  );

  /// <summary>
  /// Has the service ensure that <paramref name="tenantId"/> has a renderer and record, or joins the
  /// call already doing so: <see langword="null"/> once it has, and the record is in the directory,
  /// or the caller's error. <paramref name="cancellationToken"/> stops this request's wait, not the
  /// shared call.
  /// </summary>
  public Task<ProvisioningFailure?> EnsureAsync(
    string tenantId,
    CancellationToken cancellationToken
  )
  {
    var ensure = _ensures.GetOrAdd(
      tenantId,
      static (tenant, provisioning) =>
        new Lazy<Task<ProvisioningFailure?>>(() =>
          SharedWork.Run(() => provisioning.EnsureSharedAsync(tenant))
        ),
      this
    );
    return ensure.Value.WaitAsync(cancellationToken);
  }

  /// <summary>
  /// Has the service delete <paramref name="tenantId"/>'s renderer and record, and forgets this
  /// replica's cached record: <see langword="null"/> once it has, or the caller's error.
  /// </summary>
  public async Task<ProvisioningFailure?> DeleteAsync(
    string tenantId,
    CancellationToken cancellationToken
  )
  {
    using var timeout = new CancellationTokenSource(options.Provisioning.Timeout, timeProvider);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
      cancellationToken,
      timeout.Token
    );
    try
    {
      await client.DeleteRendererAsync(tenantId, linked.Token);
    }
    catch (Exception exception)
      when (exception is not OperationCanceledException || timeout.IsCancellationRequested)
    {
      return Fail(tenantId, "delete", exception);
    }

    directory.Evict(tenantId);
    return null;
  }

  private async Task<ProvisioningFailure?> EnsureSharedAsync(string tenantId)
  {
    try
    {
      using var timeout = new CancellationTokenSource(options.Provisioning.Timeout, timeProvider);
      LogEnsuring(logger, tenantId);
      var started = timeProvider.GetTimestamp();
      bool created;
      try
      {
        created = await client.EnsureRendererAsync(tenantId, timeout.Token);
      }
      catch (Exception exception)
      {
        return Fail(tenantId, "create", exception);
      }

      if (logger.IsEnabled(LogLevel.Information))
      {
        var elapsedMilliseconds = timeProvider.GetElapsedTime(started).TotalMilliseconds;
        LogEnsured(logger, tenantId, created ? "created" : "found", elapsedMilliseconds);
      }

      try
      {
        _ = await directory.ReloadAsync(tenantId, timeout.Token);
      }
      catch (Exception)
      {
        // The waiting requests read the store themselves, and report its failure.
      }

      return null;
    }
    finally
    {
      _ensures.TryRemove(tenantId, out _);
    }
  }

  /// <summary>Logs a refusal or failure and maps it onto the caller's error.</summary>
  private ProvisioningFailure Fail(string tenantId, string operation, Exception exception)
  {
    switch (exception)
    {
      // The gateway resolved the tenant under one of its caller's prefixes, which the service does
      // not manage: the two configurations disagree.
      case ProvisioningApiException { Kind: ProvisioningProblemKinds.NotAllowed } refusal:
        LogRefused(logger, LogLevel.Error, operation, tenantId, refusal.Kind, refusal.StatusCode);
        return new(Unavailable("The tenant's renderer is unavailable."));
      case ProvisioningApiException { Kind: ProvisioningProblemKinds.QuotaExceeded } refusal:
        LogRefused(logger, LogLevel.Warning, operation, tenantId, refusal.Kind, refusal.StatusCode);
        return new(Unavailable(QuotaFull));
      case ProvisioningApiException { Kind: ProvisioningProblemKinds.RateLimited } refusal:
        LogRefused(logger, LogLevel.Warning, operation, tenantId, refusal.Kind, refusal.StatusCode);
        return new(
          new ConversionError(
            ConversionErrorKind.Busy,
            "The provisioning service is at its rate limit. Retry later."
          ),
          refusal.RetryAfter is { } retryAfter
            ? (int)
              Math.Clamp(
                Math.Ceiling(retryAfter.TotalSeconds),
                MinRetryAfterSeconds,
                MaxRetryAfterSeconds
              )
            : null
        );
      default:
        // Failed, another answer, a service that cannot be reached, or one that did not answer
        // within Provisioning:Timeout.
        LogFailed(
          logger,
          operation,
          tenantId,
          (exception as ProvisioningApiException)?.StatusCode,
          exception
        );
        return new(
          Unavailable(
            operation == "create"
              ? "The tenant's renderer could not be created."
              : "The tenant's renderer could not be deleted."
          )
        );
    }
  }

  private static ConversionError Unavailable(string message) =>
    new(ConversionErrorKind.BrowserUnavailable, message);

  [LoggerMessage(
    EventId = 64,
    Level = LogLevel.Information,
    Message = "Asking the provisioning service for the renderer of tenant {TenantId}."
  )]
  private static partial void LogEnsuring(ILogger logger, string tenantId);

  [LoggerMessage(
    EventId = 65,
    Level = LogLevel.Information,
    Message = "The provisioning service {Outcome} the renderer of tenant {TenantId} in {ElapsedMilliseconds} ms."
  )]
  private static partial void LogEnsured(
    ILogger logger,
    string tenantId,
    string outcome,
    double elapsedMilliseconds
  );

  [LoggerMessage(
    EventId = 66,
    Message = "The provisioning service refused to {Operation} the renderer of tenant {TenantId}: {Kind} ({StatusCode})."
  )]
  private static partial void LogRefused(
    ILogger logger,
    LogLevel level,
    string operation,
    string tenantId,
    string? kind,
    int? statusCode
  );

  [LoggerMessage(
    EventId = 67,
    Level = LogLevel.Error,
    Message = "The provisioning service could not {Operation} the renderer of tenant {TenantId} (status {StatusCode})."
  )]
  private static partial void LogFailed(
    ILogger logger,
    string operation,
    string tenantId,
    int? statusCode,
    Exception exception
  );
}

/// <summary>
/// The caller's error for a refusal or failure of the provisioning service, with the
/// <c>Retry-After</c> to send in place of the kind's own, in seconds, when the service gave one.
/// </summary>
internal sealed record ProvisioningFailure(ConversionError Error, int? RetryAfterSeconds = null)
{
  /// <summary>
  /// Sends <see cref="RetryAfterSeconds"/> with the response's <c>503</c>, for a conversion, whose
  /// error <c>/convert</c> writes. Set as the response starts, after
  /// <see cref="ConversionProblems.WriteAsync"/> has set the kind's own.
  /// </summary>
  public void ApplyRetryAfter(HttpContext context)
  {
    if (RetryAfterSeconds is not { } seconds)
    {
      return;
    }

    var response = context.Response;
    response.OnStarting(() =>
    {
      if (response.StatusCode == StatusCodes.Status503ServiceUnavailable)
      {
        response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
      }

      return Task.CompletedTask;
    });
  }
}
