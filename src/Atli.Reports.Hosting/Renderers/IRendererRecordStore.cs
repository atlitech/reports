namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Where renderer records live: one per tenant. The provisioner writes it; the gateway only reads.
/// </summary>
public interface IRendererRecordStore
{
  /// <summary>Returns the tenant's record, or <see langword="null"/> when it has none.</summary>
  Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken);

  /// <summary>Returns every record.</summary>
  Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken);

  /// <summary>
  /// Creates or replaces the tenant's record in one step, so a reader sees the old record or the new
  /// one, never a mix.
  /// </summary>
  Task PutAsync(RendererRecord record, CancellationToken cancellationToken);

  /// <summary>Deletes the tenant's record. Deleting a missing record succeeds.</summary>
  Task DeleteAsync(string tenantId, CancellationToken cancellationToken);
}
