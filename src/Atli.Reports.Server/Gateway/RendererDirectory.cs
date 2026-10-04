using System.Collections.Concurrent;
using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Looks up a tenant's renderer record, reusing a found record for
/// <see cref="GatewayRecordsOptions.CacheDuration"/> and a missing one for a few seconds, so a
/// remote store (Key Vault) is not asked on every conversion. Concurrent lookups for one tenant share
/// one store call, which runs apart from the request that starts it (see <see cref="SharedWork"/>).
/// Store failures, a damaged record among them, are not cached, and affect only that tenant.
/// </summary>
/// <remarks>
/// <para>
/// A record the provisioner replaces (a rotated credential, a recreated renderer) reaches the
/// gateway when the cached one expires, or sooner: the gateway evicts a record whose renderer
/// rejected its credential, could not be reached, or names a sandbox that no longer exists, so the
/// next conversion reads the store again.
/// </para>
/// <para>
/// A record the provisioning service has just written is read with <see cref="ReloadAsync"/>, past
/// the cached missing answer and any lookup that may have started before the write. A lookup that
/// finishes after a later one never replaces the later one's answer.
/// </para>
/// </remarks>
internal sealed class RendererDirectory(
  IRendererRecordStore store,
  GatewayOptions options,
  TimeProvider timeProvider
)
{
  /// <summary>How long a tenant without a record stays without one before the store is asked again.</summary>
  private static readonly TimeSpan MissingRecordDuration = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Bounds one store call. It is shared by every request waiting for that tenant, so it must not
  /// hang forever even when each request gives up sooner.
  /// </summary>
  private static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(30);

  private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, Lazy<Task<RendererRecord?>>> _loads = new(
    StringComparer.Ordinal
  );

  /// <summary>Numbers store lookups in the order they start.</summary>
  private long _lookups;

  /// <summary>
  /// The tenant's record, or <see langword="null"/> when it has none. Throws what the store throws.
  /// </summary>
  public async Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
  {
    if (
      _entries.TryGetValue(tenantId, out var cached)
      && timeProvider.GetTimestamp() < cached.ExpiresAt
    )
    {
      return cached.Record;
    }

    var load = _loads.GetOrAdd(
      tenantId,
      static (tenant, directory) => directory.NewLoad(tenant),
      this
    );
    return await load.Value.WaitAsync(cancellationToken);
  }

  /// <summary>
  /// Reads the tenant's record from the store again, past its cached answer and any lookup already
  /// in flight, which may have started before the record was written; lookups from now on share
  /// this one. Throws what the store throws.
  /// </summary>
  public async Task<RendererRecord?> ReloadAsync(
    string tenantId,
    CancellationToken cancellationToken
  )
  {
    _entries.TryRemove(tenantId, out _);
    var load = NewLoad(tenantId);
    _loads[tenantId] = load;
    return await load.Value.WaitAsync(cancellationToken);
  }

  /// <summary>
  /// Forgets <paramref name="record"/> if it is still the tenant's cached record, so the next lookup
  /// reads the store. A record loaded since stays.
  /// </summary>
  public void Evict(string tenantId, RendererRecord record)
  {
    if (_entries.TryGetValue(tenantId, out var entry) && ReferenceEquals(entry.Record, record))
    {
      _entries.TryRemove(KeyValuePair.Create(tenantId, entry));
    }
  }

  /// <summary>
  /// Forgets the tenant's cached answer, whatever it is, so the next lookup reads the store: after
  /// the tenant's renderer was deleted.
  /// </summary>
  public void Evict(string tenantId) => _entries.TryRemove(tenantId, out _);

  /// <summary>A store lookup for the tenant, not yet started; it runs on its first use.</summary>
  private Lazy<Task<RendererRecord?>> NewLoad(string tenantId)
  {
    var lookup = Interlocked.Increment(ref _lookups);
    Lazy<Task<RendererRecord?>>? load = null;
    load = new Lazy<Task<RendererRecord?>>(() =>
      SharedWork.Run(() => LoadAsync(tenantId, lookup, load!))
    );
    return load;
  }

  private async Task<RendererRecord?> LoadAsync(
    string tenantId,
    long lookup,
    Lazy<Task<RendererRecord?>> load
  )
  {
    try
    {
      using var timeout = new CancellationTokenSource(StoreTimeout, timeProvider);
      var record = await store.GetAsync(tenantId, timeout.Token);
      var lifetime = record is null
        ? TimeSpan.FromTicks(
          Math.Min(MissingRecordDuration.Ticks, options.Records.CacheDuration.Ticks)
        )
        : options.Records.CacheDuration;
      if (lifetime > TimeSpan.Zero)
      {
        var expiresAt =
          timeProvider.GetTimestamp()
          + (long)(lifetime.TotalSeconds * timeProvider.TimestampFrequency);
        // A lookup that started earlier but finished later read an older store.
        _entries.AddOrUpdate(
          tenantId,
          static (_, entry) => entry,
          static (_, cached, entry) => cached.Lookup > entry.Lookup ? cached : entry,
          new Entry(record, expiresAt, lookup)
        );
      }

      return record;
    }
    finally
    {
      // Only this lookup: a reload may have taken its place.
      _loads.TryRemove(KeyValuePair.Create(tenantId, load));
    }
  }

  private sealed record Entry(RendererRecord? Record, long ExpiresAt, long Lookup);
}

/// <summary>
/// <c>Records:Store=Configuration</c>: the renderers listed in the gateway's own configuration, for
/// development, tests, and fixed deployments. Read-only; the provisioner cannot write to it.
/// </summary>
internal sealed class ConfigurationRendererRecordStore(GatewayOptions options)
  : IRendererRecordStore
{
  private readonly Dictionary<string, RendererRecord> _records =
    options.Records.Renderers.ToDictionary(
      renderer => renderer.TenantId,
      renderer => renderer.ToRecord(),
      StringComparer.Ordinal
    );

  public Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken) =>
    Task.FromResult(_records.GetValueOrDefault(tenantId));

  public Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken) =>
    Task.FromResult<IReadOnlyList<RendererRecord>>([.. _records.Values]);

  public Task PutAsync(RendererRecord record, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The configuration renderer store is read-only.");

  public Task DeleteAsync(string tenantId, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The configuration renderer store is read-only.");
}
