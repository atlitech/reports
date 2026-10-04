namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Where renderer records live: one per tenant. The provisioner writes it; the gateway only reads.
/// </summary>
public interface IRendererRecordStore
{
  /// <summary>Returns the tenant's record, or <see langword="null"/> when it has none.</summary>
  Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken);

  /// <summary>
  /// Returns every record. A record the store cannot read (damaged, or holding another tenant's
  /// record) is left out rather than failing the whole list; <see cref="ListWithUnreadableAsync"/>
  /// names them.
  /// </summary>
  Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken);

  /// <summary>
  /// Creates or replaces the tenant's record in one step, so a reader sees the old record or the new
  /// one, never a mix.
  /// </summary>
  Task PutAsync(RendererRecord record, CancellationToken cancellationToken);

  /// <summary>Deletes the tenant's record. Deleting a missing record succeeds.</summary>
  Task DeleteAsync(string tenantId, CancellationToken cancellationToken);

  /// <summary>
  /// Returns the tenant ID of every record, in ordinal order, reading as little as the store can:
  /// a Key Vault store lists secret names without reading any value. It includes tenants whose
  /// record <see cref="ListAsync"/> would leave out as unreadable.
  /// </summary>
  /// <remarks>
  /// For a readiness check that asks whether the store answers: it neither reads every credential
  /// nor fails because one record is damaged. Stores written before this member existed return the
  /// tenants of <see cref="ListAsync"/>.
  /// </remarks>
  async Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken cancellationToken) =>
    [
      .. (await ListAsync(cancellationToken))
        .Select(record => record.TenantId)
        .Order(StringComparer.Ordinal),
    ];

  /// <summary>
  /// Returns every record it can read, and names each one it cannot, with the reason. Failures of
  /// the store as a whole (unreachable, access denied) still throw.
  /// </summary>
  /// <remarks>
  /// The provisioner reports unreadable records, and never deletes a renderer whose record it could
  /// not read. Stores written before this member existed report none.
  /// </remarks>
  async Task<RendererRecordListing> ListWithUnreadableAsync(CancellationToken cancellationToken) =>
    new(await ListAsync(cancellationToken), []);
}

/// <summary>What <see cref="IRendererRecordStore.ListWithUnreadableAsync"/> found.</summary>
/// <param name="Records">The records it read, by tenant in ordinal order.</param>
/// <param name="Unreadable">The records it could not read, by tenant in ordinal order.</param>
public sealed record RendererRecordListing(
  IReadOnlyList<RendererRecord> Records,
  IReadOnlyList<UnreadableRendererRecord> Unreadable
);

/// <summary>A record a store holds but cannot read.</summary>
/// <param name="TenantId">The tenant the record is stored under.</param>
/// <param name="Reason">Why it cannot be read; never the record's content.</param>
public sealed record UnreadableRendererRecord(string TenantId, string Reason);
