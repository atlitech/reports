using Azure.Security.KeyVault.Secrets;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>Creates the configured <see cref="IRendererRecordStore"/>.</summary>
public static class RendererRecordStores
{
  /// <summary>
  /// Creates the store <paramref name="options"/> names: <see cref="FileRendererRecordStore"/> or
  /// <see cref="KeyVaultRendererRecordStore"/>. Throws <see cref="InvalidOperationException"/> for an
  /// unknown store or a missing setting.
  /// </summary>
  /// <remarks>
  /// Store names are case-insensitive. A Key Vault store authenticates with
  /// <see cref="AzureCredentials.Create"/> and the options' managed identity.
  /// </remarks>
  public static IRendererRecordStore Create(RendererRecordStoreOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    options.Validate();
    return options.Store == "File"
      ? new FileRendererRecordStore(options.Path)
      : new KeyVaultRendererRecordStore(
        new SecretClient(
          options.VaultUri!,
          AzureCredentials.Create(options.ManagedIdentityClientId)
        )
      );
  }
}
