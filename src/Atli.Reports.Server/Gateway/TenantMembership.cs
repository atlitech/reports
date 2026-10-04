using Atli.Reports.Hosting.Renderers;
using Microsoft.Extensions.Primitives;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Resolves the product tenant of a conversion from the authenticated caller's configured
/// membership. The tenant header is only a selector among the caller's own tenants; the body never
/// takes part.
/// </summary>
internal sealed class TenantMembership(GatewayOptions options)
{
  private readonly Dictionary<string, string[]> _tenants = options.Tenants.ToDictionary(
    membership => membership.CallerId,
    membership => membership.Tenants,
    StringComparer.Ordinal
  );

  public TenantResolution Resolve(string callerId, StringValues header)
  {
    if (!_tenants.TryGetValue(callerId, out var tenants))
    {
      return new(null, TenantRejection.NoTenants);
    }

    if (header.Count > 1)
    {
      return new(null, TenantRejection.HeaderRepeated);
    }

    // An empty value names no tenant, as if the header were absent: a client that always sends the
    // header, empty when it has nothing to name, gets the same answer as one that omits it.
    if (header.Count == 0 || string.IsNullOrEmpty(header[0]))
    {
      return tenants.Length == 1
        ? new(tenants[0], TenantRejection.None)
        : new(null, TenantRejection.HeaderRequired);
    }

    var requested = header[0];
    return Array.IndexOf(tenants, requested) >= 0
      ? new(requested, TenantRejection.None)
      : new(null, TenantRejection.NotMember);
  }
}

internal readonly record struct TenantResolution(string? TenantId, TenantRejection Rejection);

internal enum TenantRejection
{
  None,

  /// <summary>The caller belongs to no tenant: 403.</summary>
  NoTenants,

  /// <summary>The header names a tenant the caller does not belong to: 403.</summary>
  NotMember,

  /// <summary>The caller belongs to several tenants and named none (or an empty one): 400.</summary>
  HeaderRequired,

  /// <summary>The header appears more than once, so which tenant is meant is ambiguous: 400.</summary>
  HeaderRepeated,
}

/// <summary>
/// In-flight conversions per tenant in this instance, across the tenant's callers, so one tenant's
/// burst cannot hold every connection to the renderers. Full means <c>Busy</c>; nothing queues.
/// Entries disappear with their last request, and only configured tenants ever get one.
/// </summary>
internal sealed class TenantAdmission
{
  private readonly Lock _gate = new();
  private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);

  /// <summary>
  /// Takes one of the tenant's <paramref name="limit"/> slots: the gateway's
  /// <c>MaxConcurrentRequestsPerTenant</c>, or less when this replica's share of what the tenant's
  /// renderer admits is fewer.
  /// </summary>
  public bool TryAcquire(string tenantId, int limit, out IDisposable? lease)
  {
    lock (_gate)
    {
      var count = _active.GetValueOrDefault(tenantId);
      if (count >= limit)
      {
        lease = null;
        return false;
      }

      _active[tenantId] = count + 1;
      lease = new Lease(this, tenantId);
      return true;
    }
  }

  private void Release(string tenantId)
  {
    lock (_gate)
    {
      var count = _active[tenantId];
      if (count == 1)
      {
        _active.Remove(tenantId);
      }
      else
      {
        _active[tenantId] = count - 1;
      }
    }
  }

  private sealed class Lease(TenantAdmission owner, string tenantId) : IDisposable
  {
    private int _disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, 1) == 0)
      {
        owner.Release(tenantId);
      }
    }
  }
}

/// <summary>
/// The request's verified tenant, set by the gateway middleware for the converter, with the
/// renderer record the middleware read for the tenant's limit, so the conversion does not read it
/// again. <paramref name="RecordLoaded"/> is <see langword="false"/> when that read failed; the
/// converter then reads the record itself and reports the failure.
/// </summary>
internal sealed record GatewayTenantFeature(
  string TenantId,
  RendererRecord? Record = null,
  bool RecordLoaded = false
);
