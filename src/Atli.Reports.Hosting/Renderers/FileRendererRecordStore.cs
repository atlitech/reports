// Stub: the implementation replaces this file, and this pragma with it.
#pragma warning disable CS9113
namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Records as one JSON file per tenant in a directory, written atomically (write, then rename) and
/// readable by their owner only. For development, tests, and single-machine deployments.
/// </summary>
/// <param name="directory">The directory; created when missing.</param>
public sealed class FileRendererRecordStore(string directory) : IRendererRecordStore
{
  /// <inheritdoc />
  public Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task PutAsync(RendererRecord record, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task DeleteAsync(string tenantId, CancellationToken cancellationToken) =>
    throw new NotImplementedException();
}
