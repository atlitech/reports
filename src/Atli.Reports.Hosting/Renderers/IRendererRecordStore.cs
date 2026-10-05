namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Where renderer records live: one per tenant. The provisioner writes it; the gateway only reads.
/// </summary>
public interface IRendererRecordStore
{
  /// <summary>Returns the tenant's record, or <see langword="null"/> when it has none.</summary>
  Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken);

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
  /// record cannot be read.
  /// </summary>
  /// <remarks>
  /// For a readiness check that asks whether the store answers: it neither reads every credential
  /// nor fails because one record is damaged.
  /// </remarks>
  Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken cancellationToken);

  /// <summary>
  /// Returns every record it can read, and names each one it cannot, with the reason. Failures of
  /// the store as a whole (unreachable, access denied) still throw.
  /// </summary>
  /// <remarks>
  /// The provisioner reports unreadable records, and never deletes a renderer whose record it could
  /// not read.
  /// </remarks>
  Task<RendererRecordListing> ListWithUnreadableAsync(CancellationToken cancellationToken);
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
