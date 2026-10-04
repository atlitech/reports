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
}
