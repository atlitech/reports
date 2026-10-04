// Stub: the implementation replaces this file, and this pragma with it.
#pragma warning disable CS9113
using Azure.Security.KeyVault.Secrets;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Records as Key Vault secrets named <c>renderer-&lt;tenant&gt;</c>, each holding the record as JSON. A
/// new secret version replaces a record in one step. The gateway needs only to get and list secrets.
/// </summary>
/// <param name="client">The vault's secret client.</param>
public sealed class KeyVaultRendererRecordStore(SecretClient client) : IRendererRecordStore
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
