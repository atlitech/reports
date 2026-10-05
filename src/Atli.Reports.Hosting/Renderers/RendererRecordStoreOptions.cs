namespace Atli.Reports.Hosting.Renderers;

/// <summary>Which <see cref="IRendererRecordStore"/> to use, and where it keeps records.</summary>
public sealed class RendererRecordStoreOptions
{
  /// <summary><c>File</c> (development and tests) or <c>KeyVault</c>.</summary>
  public string Store { get; set; } = "";

  /// <summary>For <c>File</c>: the directory of record files, readable by their owner only.</summary>
  public string Path { get; set; } = "";

  /// <summary>For <c>KeyVault</c>: the vault, such as <c>https://contoso.vault.azure.net/</c>.</summary>
  public Uri? VaultUri { get; set; }

  /// <summary>For <c>KeyVault</c>: a user-assigned managed identity's client ID; empty for the default chain.</summary>
  public string ManagedIdentityClientId { get; set; } = "";

  /// <summary>Checks the store and its address, and normalizes the case-insensitive store name.</summary>
  public void Validate()
  {
    if (string.Equals(Store, "File", StringComparison.OrdinalIgnoreCase))
    {
      Store = "File";
      if (string.IsNullOrWhiteSpace(Path))
      {
        throw new InvalidOperationException(
          "The File renderer record store needs Path, the directory of record files."
        );
      }
      return;
    }

    if (string.Equals(Store, "KeyVault", StringComparison.OrdinalIgnoreCase))
    {
      Store = "KeyVault";
      if (VaultUri is not { IsAbsoluteUri: true } || VaultUri.Scheme != Uri.UriSchemeHttps)
      {
        throw new InvalidOperationException(
          "The KeyVault renderer record store needs VaultUri, an absolute HTTPS address such as https://contoso.vault.azure.net/."
        );
      }
      return;
    }

    throw new InvalidOperationException(
      string.IsNullOrWhiteSpace(Store)
        ? "No renderer record store is configured. Set Store to File or KeyVault."
        : $"Unknown renderer record store '{Store}'. Use File or KeyVault."
    );
  }
}
