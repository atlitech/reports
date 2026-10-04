using Atli.Reports.Hosting.Renderers;
using Microsoft.Extensions.Primitives;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Resolves the product tenant of a conversion from the authenticated caller's configured
/// membership: the tenants it lists, and every valid tenant ID under one of its tenant prefixes.
/// The tenant header is only a selector among the caller's own tenants; the body never takes part.
/// </summary>
internal sealed class TenantMembership(GatewayOptions options)
{
  private readonly Dictionary<string, Membership> _callers = options.Tenants.ToDictionary(
    membership => membership.CallerId,
    membership => new Membership(membership.Tenants, membership.TenantPrefixes),
    StringComparer.Ordinal
  );

  public TenantResolution Resolve(string callerId, StringValues header)
  {
    if (!_callers.TryGetValue(callerId, out var membership))
    {
      return new(null, TenantRejection.NoTenants);
    }

    if (header.Count > 1)
    {
      return new(null, TenantRejection.HeaderRepeated);
    }

    // An empty value names no tenant, as if the header were absent: a client that always sends the
    // header, empty when it has nothing to name, gets the same answer as one that omits it. Only a
    // caller with one tenant and no prefix has a tenant to fall back on.
    if (header.Count == 0 || string.IsNullOrEmpty(header[0]))
    {
      return membership is { Tenants.Length: 1, Prefixes.Length: 0 }
        ? new(membership.Tenants[0], TenantRejection.None)
        : new(null, TenantRejection.HeaderRequired);
    }

    // A listed tenant is the operator's. Validation keeps listed tenants out of every prefix, so
    // no tenant is both.
    var requested = header[0];
    if (Array.IndexOf(membership.Tenants, requested) >= 0)
    {
      return new(requested, TenantRejection.None);
    }

    // A caller has few prefixes, so a linear scan is cheap.
    foreach (var prefix in membership.Prefixes)
    {
      if (TenantPrefix.Owns(prefix, requested))
      {
        return new(requested, TenantRejection.None, ViaPrefix: true);
      }
    }

    return new(null, TenantRejection.NotMember);
  }

  private sealed record Membership(string[] Tenants, string[] Prefixes);
}

/// <param name="TenantId">The verified tenant, or <see langword="null"/> when rejected.</param>
/// <param name="Rejection">Why the tenant was rejected, or <see cref="TenantRejection.None"/>.</param>
/// <param name="ViaPrefix">
/// Whether the tenant is the caller's through one of its <c>TenantPrefixes</c> rather than listed in
/// its <c>Tenants</c>: an application-managed tenant, whose renderer the gateway may ask the
/// provisioning service to create or delete.
/// </param>
internal readonly record struct TenantResolution(
  string? TenantId,
  TenantRejection Rejection,
  bool ViaPrefix = false
);

internal enum TenantRejection
{
  None,

  /// <summary>The caller belongs to no tenant: 403.</summary>
  NoTenants,

  /// <summary>The header names a tenant the caller does not belong to: 403.</summary>
  NotMember,

  /// <summary>
  /// The caller belongs to several tenants, or has a tenant prefix, and named none (or an empty
  /// one): 400.
  /// </summary>
  HeaderRequired,

  /// <summary>The header appears more than once, so which tenant is meant is ambiguous: 400.</summary>
  HeaderRepeated,
}

/// <summary>
/// Requests in flight per key in this instance, each key up to the limit its caller passes:
/// conversions per tenant across the tenant's callers, so one tenant's burst cannot hold every
/// connection to the renderers, and deletions per caller, so no caller can hold any number of
/// calls to the provisioning service open. Full means <c>Busy</c>; nothing queues. An entry lives
/// only while its key has a request in flight, so there are never more entries than requests in
/// flight, however many tenants callers name under their prefixes.
/// </summary>
internal sealed class InFlightAdmission
{
  private readonly Lock _gate = new();
  private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);

  /// <summary>
  /// Takes one of the key's <paramref name="limit"/> slots: for a tenant, the gateway's
  /// <c>MaxConcurrentRequestsPerTenant</c>, or less when this replica's share of what the tenant's
  /// renderer admits is fewer; for a caller's deletions, <c>MaxConcurrentDeletesPerCaller</c>.
  /// </summary>
  public bool TryAcquire(string key, int limit, out IDisposable? lease)
  {
    lock (_gate)
    {
      var count = _active.GetValueOrDefault(key);
      if (count >= limit)
      {
        lease = null;
        return false;
      }

      _active[key] = count + 1;
      lease = new Lease(this, key);
      return true;
    }
  }

  private void Release(string key)
  {
    lock (_gate)
    {
      var count = _active[key];
      if (count == 1)
      {
        _active.Remove(key);
      }
      else
      {
        _active[key] = count - 1;
      }
    }
  }

  private sealed class Lease(InFlightAdmission owner, string key) : IDisposable
  {
    private int _disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, 1) == 0)
      {
        owner.Release(key);
      }
    }
  }
}

/// <summary>
/// The request's verified tenant, set by the gateway middleware for the converter, with the
/// renderer record the middleware read for the tenant's limit, so the conversion does not read it
/// again. <paramref name="RecordLoaded"/> is <see langword="false"/> when that read failed; the
/// converter then reads the record itself and reports the failure. <paramref name="ViaPrefix"/> is
/// <see cref="TenantResolution.ViaPrefix"/>.
/// </summary>
internal sealed record GatewayTenantFeature(
  string TenantId,
  RendererRecord? Record = null,
  bool RecordLoaded = false,
  bool ViaPrefix = false
);
