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
/// the cached missing answer and any lookup that may have started before the write. A record it has
/// just deleted is forgotten with <see cref="Evict(string)"/>. A lookup that finishes after a later
/// one, or after an eviction, never replaces the later one's answer: it may have read the store
/// before the write or the deletion.
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

  /// <summary>
  /// How often expired answers are cleared out. A caller with a tenant prefix can name any number of
  /// tenants, so answers must not outlive their use.
  /// </summary>
  private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

  /// <summary>
  /// How long an expired record is kept after it expires, so that refreshing it does not count as a
  /// new tenant's lookup (see <see cref="IsKnown"/>). Missing answers and evictions go at expiry.
  /// </summary>
  private static readonly TimeSpan KnownRecordRetention = TimeSpan.FromMinutes(10);

  private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, Lazy<Task<RendererRecord?>>> _loads = new(
    StringComparer.Ordinal
  );

  /// <summary>Numbers store lookups in the order they start.</summary>
  private long _lookups;

  /// <summary>When expired answers are next cleared out, as a <see cref="TimeProvider"/> timestamp.</summary>
  private long _nextSweep;

  /// <summary>
  /// The tenant's record, or <see langword="null"/> when it has none. Throws what the store throws.
  /// </summary>
  public async Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
  {
    if (_entries.TryGetValue(tenantId, out var cached) && IsFresh(cached))
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
  /// Whether the tenant is known to the gateway, so that looking it up costs its caller none of the
  /// budget for new tenants: an answer, a record or none, is cached, a lookup is in flight for it to
  /// join, or its last answer was a record, which has since expired. Refreshing a tenant that had a
  /// renderer reads the store, but such tenants are as many as renderers, which the provisioning
  /// service's quotas bound, unlike the tenant IDs a caller can make up.
  /// </summary>
  public bool IsKnown(string tenantId) =>
    (
      _entries.TryGetValue(tenantId, out var cached)
      && (IsFresh(cached) || (!cached.Evicted && cached.Record is not null))
    ) || _loads.ContainsKey(tenantId);

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
    Evict(tenantId);
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
  /// the tenant's renderer was deleted. Every lookup already started may have read the store before
  /// the deletion, so none of them is cached when it finishes, and lookups from now on start their
  /// own rather than join one of them.
  /// </summary>
  public void Evict(string tenantId)
  {
    // Numbered after every lookup started so far, so only a later one replaces it, and never taken
    // as an answer. It outlasts the store calls it must outrank, which StoreTimeout bounds.
    var evicted = new Entry(
      null,
      timeProvider.GetTimestamp()
        + (long)(StoreTimeout.TotalSeconds * timeProvider.TimestampFrequency),
      Interlocked.Increment(ref _lookups),
      Evicted: true
    );
    _entries.AddOrUpdate(
      tenantId,
      static (_, entry) => entry,
      static (_, cached, entry) => cached.Lookup > entry.Lookup ? cached : entry,
      evicted
    );
    _loads.TryRemove(tenantId, out _);
  }

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
        // A lookup that started earlier but finished later, or that an eviction overtook, read an
        // older store.
        _entries.AddOrUpdate(
          tenantId,
          static (_, entry) => entry,
          static (_, cached, entry) => cached.Lookup > entry.Lookup ? cached : entry,
          new Entry(record, expiresAt, lookup)
        );
      }

      SweepExpired();
      return record;
    }
    finally
    {
      // Only this lookup: a reload may have taken its place.
      _loads.TryRemove(KeyValuePair.Create(tenantId, load));
    }
  }

  /// <summary>Removes expired answers, at most once per <see cref="SweepInterval"/>.</summary>
  private void SweepExpired()
  {
    var now = timeProvider.GetTimestamp();
    var due = Interlocked.Read(ref _nextSweep);
    var next = now + (long)(SweepInterval.TotalSeconds * timeProvider.TimestampFrequency);
    if (now < due || Interlocked.CompareExchange(ref _nextSweep, next, due) != due)
    {
      return;
    }

    var retention = (long)(KnownRecordRetention.TotalSeconds * timeProvider.TimestampFrequency);
    foreach (var entry in _entries)
    {
      var keptUntil =
        entry.Value.Record is not null && !entry.Value.Evicted
          ? entry.Value.ExpiresAt + retention
          : entry.Value.ExpiresAt;
      if (now >= keptUntil)
      {
        // Only this answer: a newer one may have replaced it meanwhile.
        _entries.TryRemove(entry);
      }
    }
  }

  /// <summary>Whether <paramref name="entry"/> answers a lookup now.</summary>
  private bool IsFresh(Entry entry) =>
    !entry.Evicted && timeProvider.GetTimestamp() < entry.ExpiresAt;

  /// <param name="Record">The tenant's record, or <see langword="null"/> when it has none.</param>
  /// <param name="ExpiresAt">When the entry stops answering and may be cleared out.</param>
  /// <param name="Lookup">The number of the lookup that read it, or of the eviction.</param>
  /// <param name="Evicted">
  /// Whether this marks an eviction rather than an answer: it answers no lookup, and only keeps
  /// earlier lookups from being cached.
  /// </param>
  private sealed record Entry(
    RendererRecord? Record,
    long ExpiresAt,
    long Lookup,
    bool Evicted = false
  );
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
