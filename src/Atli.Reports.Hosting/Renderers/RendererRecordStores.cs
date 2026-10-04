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
    if (string.Equals(options.Store, "File", StringComparison.OrdinalIgnoreCase))
    {
      if (string.IsNullOrWhiteSpace(options.Path))
      {
        throw new InvalidOperationException(
          "The File renderer record store needs Path, the directory of record files."
        );
      }

      return new FileRendererRecordStore(options.Path);
    }

    if (string.Equals(options.Store, "KeyVault", StringComparison.OrdinalIgnoreCase))
    {
      if (options.VaultUri is not { IsAbsoluteUri: true } vaultUri)
      {
        throw new InvalidOperationException(
          "The KeyVault renderer record store needs VaultUri, such as https://contoso.vault.azure.net/."
        );
      }

      return new KeyVaultRendererRecordStore(
        new SecretClient(vaultUri, AzureCredentials.Create(options.ManagedIdentityClientId))
      );
    }

    throw new InvalidOperationException(
      string.IsNullOrWhiteSpace(options.Store)
        ? "No renderer record store is configured. Set Store to File or KeyVault."
        : $"Unknown renderer record store '{options.Store}'. Use File or KeyVault."
    );
  }
}
