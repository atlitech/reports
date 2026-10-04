using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>
/// A record store over another that counts, and can fail, its listings of tenant IDs: what the
/// provisioning service lists for its quotas and readiness.
/// </summary>
internal sealed class ListingRecordStore(IRendererRecordStore inner) : IRendererRecordStore
{
  private int _listings;

  /// <summary>Fails every listing of tenant IDs with this exception, when set.</summary>
  public Exception? FailListing { get; set; }

  /// <summary>How many times the tenant IDs were listed.</summary>
  public int Listings => Volatile.Read(ref _listings);

  public async Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken cancellationToken)
  {
    Interlocked.Increment(ref _listings);
    if (FailListing is { } failure)
    {
      throw failure;
    }

    return await inner.ListTenantIdsAsync(cancellationToken);
  }

  public Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken) =>
    inner.GetAsync(tenantId, cancellationToken);

  public Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken) =>
    inner.ListAsync(cancellationToken);

  public Task<RendererRecordListing> ListWithUnreadableAsync(CancellationToken cancellationToken) =>
    inner.ListWithUnreadableAsync(cancellationToken);

  public Task PutAsync(RendererRecord record, CancellationToken cancellationToken) =>
    inner.PutAsync(record, cancellationToken);

  public Task DeleteAsync(string tenantId, CancellationToken cancellationToken) =>
    inner.DeleteAsync(tenantId, cancellationToken);
}
