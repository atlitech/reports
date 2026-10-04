using System.Globalization;
using System.Text.Json.Serialization;
using Atli.Reports.Hosting.Provisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// <c>PUT</c> and <c>DELETE</c> <see cref="ProvisioningApi.RendererRoute"/>, answered as
/// <see cref="ProvisioningApi"/> describes.
/// </summary>
internal static class RendererEndpoints
{
  public static void MapRendererEndpoints(this IEndpointRouteBuilder endpoints)
  {
    endpoints.MapPut(
      ProvisioningApi.RendererRoute,
      async (string tenantId, HttpContext context, ManagedRenderers renderers) =>
        Answer(context, tenantId, await renderers.EnsureAsync(tenantId, context.RequestAborted))
    );
    endpoints.MapDelete(
      ProvisioningApi.RendererRoute,
      async (string tenantId, HttpContext context, ManagedRenderers renderers) =>
        Answer(context, tenantId, await renderers.DeleteAsync(tenantId, context.RequestAborted))
    );
  }

  private static IResult Answer(HttpContext context, string tenantId, RendererOutcome outcome) =>
    outcome.Answer switch
    {
      RendererAnswer.Found or RendererAnswer.Created => TypedResults.Json(
        new EnsureRendererResponse
        {
          TenantId = tenantId,
          Created = outcome.Answer == RendererAnswer.Created,
        },
        ProvisioningJsonContext.Default.EnsureRendererResponse
      ),
      RendererAnswer.Deleted => TypedResults.NoContent(),
      RendererAnswer.InvalidTenantId => ServiceProblems.Problem(
        StatusCodes.Status400BadRequest,
        ProvisioningProblemKinds.InvalidRequest,
        "The tenant ID is not one.",
        "A tenant ID is 1 to 63 lowercase letters, digits, and hyphens, starting with a letter or "
          + "digit."
      ),
      RendererAnswer.NotAllowed => ServiceProblems.Problem(
        StatusCodes.Status403Forbidden,
        ProvisioningProblemKinds.NotAllowed,
        "The tenant is not managed by this service.",
        $"Tenant {tenantId} is under none of the prefixes this service manages."
      ),
      RendererAnswer.QuotaExceeded => ServiceProblems.Problem(
        StatusCodes.Status429TooManyRequests,
        ProvisioningProblemKinds.QuotaExceeded,
        "The tenant's prefix has its most renderers.",
        "No renderer is created under the prefix until tenants under it are deleted or retired."
      ),
      RendererAnswer.RateLimited => RateLimited(context, outcome.RetryAfter),
      _ => ServiceProblems.Problem(
        StatusCodes.Status503ServiceUnavailable,
        ProvisioningProblemKinds.Failed,
        "The renderer could not be made ready.",
        "Try again later."
      ),
    };

  private static IResult RateLimited(HttpContext context, TimeSpan retryAfter)
  {
    // Whole seconds, rounded up, so a retry at Retry-After is never early.
    var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
    context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
    return ServiceProblems.Problem(
      StatusCodes.Status429TooManyRequests,
      ProvisioningProblemKinds.RateLimited,
      "Too many renderers were created in the last minute.",
      "Try again after Retry-After."
    );
  }
}

/// <summary>
/// The service's problem details (RFC 9457), each with one of <see cref="ProvisioningProblemKinds"/>
/// as its <c>kind</c>.
/// </summary>
internal static class ServiceProblems
{
  public static IResult Problem(int status, string kind, string title, string detail) =>
    TypedResults.Problem(
      detail: detail,
      statusCode: status,
      title: title,
      extensions: new Dictionary<string, object?> { ["kind"] = kind }
    );

  /// <summary>
  /// Completes every problem the service writes, those of status code pages (the <c>401</c> of a
  /// challenge, an unknown route) and the exception handler included, with a <c>kind</c>.
  /// </summary>
  public static void Customize(ProblemDetailsContext context)
  {
    var problem = context.ProblemDetails;
    var status = problem.Status ?? context.HttpContext.Response.StatusCode;
    problem.Extensions.TryAdd(
      "kind",
      status switch
      {
        StatusCodes.Status401Unauthorized => ProvisioningProblemKinds.Unauthorized,
        StatusCodes.Status403Forbidden => ProvisioningProblemKinds.NotAllowed,
        >= 500 => ProvisioningProblemKinds.Failed,
        _ => ProvisioningProblemKinds.InvalidRequest,
      }
    );
    if (status == StatusCodes.Status401Unauthorized)
    {
      problem.Detail ??= $"Send the gateway's credential in {ProvisioningApi.ApiKeyHeader}.";
    }
  }
}

/// <summary>The service's own JSON, source-generated: its problem details.</summary>
[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(ProblemDetails))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;
