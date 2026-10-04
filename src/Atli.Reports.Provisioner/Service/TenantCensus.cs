using Atli.Reports.Hosting.Renderers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// Which tenants have a record, for each prefix's <c>MaxTenants</c>: the record store's last
/// listing (<see cref="IRendererRecordStore.ListTenantIdsAsync"/>), which lists only names, with
/// the records the service itself wrote or deleted since that listing started on top. A listing is
/// used for <see cref="MaxAge"/>, so others' changes (a <c>delete</c> or <c>create</c> from the
/// command line, another replica) count from the next one.
/// </summary>
internal sealed class TenantCensus(IRendererRecordStore records, TimeProvider time) : IDisposable
{
  /// <summary>How long a listing is used before the store is listed again.</summary>
  public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

  private readonly Lock _lock = new();
  private readonly SemaphoreSlim _listing = new(1, 1);

  // By tenant: whether the service last wrote (true) or deleted (false) its record, and when.
  private readonly Dictionary<string, (bool Exists, long At)> _changes = new(
    StringComparer.Ordinal
  );

  private HashSet<string>? _listed;
  private long _listedAt;
  private bool _stale;

  /// <summary>
  /// Lists the store's tenants again unless the last listing started less than
  /// <see cref="MaxAge"/> ago. Throws what the store throws; one listing runs at a time.
  /// </summary>
  public async Task RefreshAsync(CancellationToken cancellationToken)
  {
    if (IsFresh())
    {
      return;
    }

    await _listing.WaitAsync(cancellationToken);
    try
    {
      if (IsFresh())
      {
        return;
      }

      var startedAt = time.GetTimestamp();
      var tenantIds = await records.ListTenantIdsAsync(cancellationToken);
      lock (_lock)
      {
        _listed = new HashSet<string>(tenantIds, StringComparer.Ordinal);
        _listedAt = startedAt;
        _stale = false;
        // A change made before the listing started is in it.
        foreach (var (tenantId, change) in _changes.ToArray())
        {
          if (change.At < startedAt)
          {
            _changes.Remove(tenantId);
          }
        }
      }
    }
    finally
    {
      _listing.Release();
    }
  }

  /// <summary>The service wrote, or found, the tenant's record.</summary>
  public void Recorded(string tenantId) => Change(tenantId, exists: true);

  /// <summary>The service deleted the tenant's record.</summary>
  public void Deleted(string tenantId) => Change(tenantId, exists: false);

  /// <summary>
  /// Lists the store again on the next <see cref="RefreshAsync"/>, such as after a retirement run;
  /// until then the last listing still counts.
  /// </summary>
  public void Invalidate()
  {
    lock (_lock)
    {
      _stale = true;
    }
  }

  /// <summary>
  /// How many tenants under <paramref name="prefix"/> other than <paramref name="except"/> have a
  /// record or are among <paramref name="pending"/>, the creations in flight. Call after
  /// <see cref="RefreshAsync"/>.
  /// </summary>
  public int CountUnder(string prefix, string except, IReadOnlyCollection<string> pending)
  {
    lock (_lock)
    {
      HashSet<string> tenantIds = [.. _listed ?? [], .. _changes.Keys, .. pending];
      return tenantIds.Count(tenantId =>
        tenantId != except
        && TenantPrefix.Owns(prefix, tenantId)
        && (pending.Contains(tenantId) || Exists(tenantId))
      );
    }
  }

  public void Dispose() => _listing.Dispose();

  private bool Exists(string tenantId) =>
    _changes.TryGetValue(tenantId, out var change)
      ? change.Exists
      : _listed?.Contains(tenantId) == true;

  private void Change(string tenantId, bool exists)
  {
    lock (_lock)
    {
      _changes[tenantId] = (exists, time.GetTimestamp());
    }
  }

  private bool IsFresh()
  {
    lock (_lock)
    {
      return _listed is not null && !_stale && time.GetElapsedTime(_listedAt) < MaxAge;
    }
  }
}

/// <summary>
/// Readiness: whether the record store answers, as <see cref="TenantCensus.RefreshAsync"/> asks it,
/// so a probe lists the store at most once per <see cref="TenantCensus.MaxAge"/>.
/// </summary>
internal sealed class RecordStoreHealthCheck(TenantCensus census) : IHealthCheck
{
  public async Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default
  )
  {
    try
    {
      await census.RefreshAsync(cancellationToken);
      return HealthCheckResult.Healthy();
    }
    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
    {
      return HealthCheckResult.Unhealthy("The renderer record store did not answer.", exception);
    }
  }
}
