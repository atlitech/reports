using System.Text.Json.Serialization;

namespace Atli.Reports.Hosting.Provisioning;

/// <summary>
/// The provisioning service's HTTP API (<c>atli-reports-provisioner serve</c>). The gateway calls it
/// to create a renderer for a tenant on that tenant's first conversion, and to delete one when the
/// application deletes the tenant. The service holds the sandbox group's Data Owner role, which the
/// gateway never does; it serves only tenants under the prefixes it is configured to manage, within
/// their quotas, so a compromised gateway can at most create renderers there, never change, reassign,
/// or enter existing ones. Reachable on internal ingress only.
/// </summary>
/// <remarks>
/// <para>Every request carries the gateway's credential in <see cref="ApiKeyHeader"/>; a missing or
/// wrong one is <c>401</c>.</para>
/// <para><c>PUT /tenants/{tenantId}/renderer</c> ensures the tenant has a ready renderer and record,
/// and answers <c>200</c> with <see cref="EnsureRendererResponse"/> once it does: at once when the
/// record exists and names a sandbox that exists (<c>created: false</c>, a disabled sandbox
/// included, since the kill switch wins), or after creating one when there is no record or its
/// sandbox no longer exists (<c>created: true</c>). Concurrent calls for one tenant share one
/// creation.</para>
/// <para><c>DELETE /tenants/{tenantId}/renderer</c> deletes the tenant's record and then every sandbox
/// labeled for it, and answers <c>204</c>, also when there was nothing to delete.</para>
/// <para>Failures are RFC 9457 problem details whose <c>kind</c> is one of
/// <see cref="ProvisioningProblemKinds"/>: <c>400</c>
/// <see cref="ProvisioningProblemKinds.InvalidRequest"/> for a tenant ID that is not one, <c>401</c>
/// <see cref="ProvisioningProblemKinds.Unauthorized"/>, <c>403</c>
/// <see cref="ProvisioningProblemKinds.NotAllowed"/>, <c>429</c>
/// <see cref="ProvisioningProblemKinds.QuotaExceeded"/> or
/// <see cref="ProvisioningProblemKinds.RateLimited"/> (with <c>Retry-After</c>), and <c>503</c>
/// <see cref="ProvisioningProblemKinds.Failed"/>.</para>
/// </remarks>
public static class ProvisioningApi
{
  /// <summary>The header of the gateway's credential, <c>&lt;id&gt;.&lt;secret&gt;</c>, as the server's API keys.</summary>
  public const string ApiKeyHeader = "X-Reports-Api-Key";

  /// <summary>The route of one tenant's renderer: <c>PUT</c> ensures it, <c>DELETE</c> deletes it.</summary>
  public const string RendererRoute = "/tenants/{tenantId}/renderer";

  /// <summary>The path of <paramref name="tenantId"/>'s renderer, which must be a valid tenant ID.</summary>
  public static string RendererPath(string tenantId) =>
    $"/tenants/{Renderers.TenantId.Validate(tenantId)}/renderer";
}

/// <summary>The <c>kind</c> of a provisioning service's problem details.</summary>
public static class ProvisioningProblemKinds
{
  /// <summary><c>400</c>: a tenant ID that is not one, or a route the service does not have.</summary>
  public const string InvalidRequest = "InvalidRequest";

  /// <summary><c>401</c>: no credential, or not one of the gateway's.</summary>
  public const string Unauthorized = "Unauthorized";

  /// <summary>
  /// <c>403</c>: the tenant is under none of the prefixes the service manages. Also a sign that the
  /// gateway's and the service's configurations disagree.
  /// </summary>
  public const string NotAllowed = "NotAllowed";

  /// <summary>
  /// <c>429</c> without <c>Retry-After</c>: the tenant's prefix already has its most renderers
  /// (<c>MaxTenants</c>). Lasts until renderers are deleted or retired.
  /// </summary>
  public const string QuotaExceeded = "QuotaExceeded";

  /// <summary><c>429</c> with <c>Retry-After</c>: the service creates no more renderers per minute.</summary>
  public const string RateLimited = "RateLimited";

  /// <summary>
  /// <c>409</c>: the tenant's renderer is disabled (the operator's kill switch), so the service
  /// neither deletes nor replaces it; the operator enables or deletes it.
  /// </summary>
  public const string Disabled = "Disabled";

  /// <summary>
  /// <c>503</c>: the renderer could not be created now (the data plane, the record store, or the
  /// renderer's readiness failed), or the tenant's record cannot be read. May succeed later.
  /// </summary>
  public const string Failed = "Failed";
}

/// <summary>The body of a successful <c>PUT /tenants/{tenantId}/renderer</c>.</summary>
public sealed class EnsureRendererResponse
{
  /// <summary>The tenant.</summary>
  public required string TenantId { get; init; }

  /// <summary>Whether this call created the renderer, rather than finding one.</summary>
  public bool Created { get; init; }
}

/// <summary>The provisioning API's JSON, for the service and its client.</summary>
[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(EnsureRendererResponse))]
public sealed partial class ProvisioningJsonContext : JsonSerializerContext;
