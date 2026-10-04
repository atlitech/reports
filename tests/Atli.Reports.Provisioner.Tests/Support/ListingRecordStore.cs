using System.Collections.Concurrent;
using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>
/// A record store over another that counts, and can fail or hold, its listings of tenant IDs: what
/// the provisioning service lists for its quotas and readiness. It also notes every record read.
/// </summary>
internal sealed class ListingRecordStore(IRendererRecordStore inner) : IRendererRecordStore
{
  private readonly ConcurrentQueue<string> _gets = new();
  private readonly TaskCompletionSource _held = new(
    TaskCreationOptions.RunContinuationsAsynchronously
  );
  private int _listings;

  /// <summary>Fails every listing of tenant IDs with this exception, when set.</summary>
  public Exception? FailListing { get; set; }

  /// <summary>Holds every listing of tenant IDs until this task completes, when set.</summary>
  public Task? HoldListing { get; set; }

  /// <summary>Completes once a listing is held by <see cref="HoldListing"/>.</summary>
  public Task Held => _held.Task;

  /// <summary>How many times the tenant IDs were listed, held listings included.</summary>
  public int Listings => Volatile.Read(ref _listings);

  /// <summary>The tenants whose records were read, in order.</summary>
  public IReadOnlyList<string> Gets => [.. _gets];

  public async Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken cancellationToken)
  {
    Interlocked.Increment(ref _listings);
    if (HoldListing is { } hold)
    {
      _held.TrySetResult();
      await hold.WaitAsync(cancellationToken);
    }

    if (FailListing is { } failure)
    {
      throw failure;
    }

    return await inner.ListTenantIdsAsync(cancellationToken);
  }

  public Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
  {
    _gets.Enqueue(tenantId);
    return inner.GetAsync(tenantId, cancellationToken);
  }

  public Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken) =>
    inner.ListAsync(cancellationToken);

  public Task<RendererRecordListing> ListWithUnreadableAsync(CancellationToken cancellationToken) =>
    inner.ListWithUnreadableAsync(cancellationToken);

  public Task PutAsync(RendererRecord record, CancellationToken cancellationToken) =>
    inner.PutAsync(record, cancellationToken);

  public Task DeleteAsync(string tenantId, CancellationToken cancellationToken) =>
    inner.DeleteAsync(tenantId, cancellationToken);
}
