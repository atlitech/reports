using Azure.Security.KeyVault.Secrets;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// The secret operations <see cref="KeyVaultRendererRecordStore"/> makes, so tests can stand in for
/// a vault. Failures surface as the vault's <see cref="Azure.RequestFailedException"/>, whose
/// status the store interprets.
/// </summary>
internal interface IRendererSecrets
{
  /// <summary>Gets the secret's current version.</summary>
  Task<KeyVaultSecret> GetSecretAsync(string name, CancellationToken cancellationToken);

  /// <summary>Lists every secret's properties, values left out.</summary>
  IAsyncEnumerable<SecretProperties> GetPropertiesOfSecretsAsync(
    CancellationToken cancellationToken
  );

  /// <summary>Adds a version of the secret, creating the secret when it has none.</summary>
  Task SetSecretAsync(KeyVaultSecret secret, CancellationToken cancellationToken);

  /// <summary>
  /// Disables the secret's current version. Fails with <c>404</c> when there is no secret, and as a
  /// get does (<c>403</c>, <c>SecretDisabled</c>) when that version is disabled already.
  /// </summary>
  Task DisableSecretAsync(string name, CancellationToken cancellationToken);

  /// <summary>Starts deleting the secret; the vault keeps it soft-deleted for its retention period.</summary>
  Task StartDeleteSecretAsync(string name, CancellationToken cancellationToken);

  /// <summary>Recovers a soft-deleted secret, and returns once it can be read and written again.</summary>
  Task RecoverDeletedSecretAsync(string name, CancellationToken cancellationToken);
}

/// <summary><see cref="IRendererSecrets"/> over a vault's <see cref="SecretClient"/>.</summary>
internal sealed class SecretClientRendererSecrets(SecretClient client) : IRendererSecrets
{
  public async Task<KeyVaultSecret> GetSecretAsync(
    string name,
    CancellationToken cancellationToken
  ) => await client.GetSecretAsync(name, cancellationToken: cancellationToken);

  public IAsyncEnumerable<SecretProperties> GetPropertiesOfSecretsAsync(
    CancellationToken cancellationToken
  ) => client.GetPropertiesOfSecretsAsync(cancellationToken);

  public Task SetSecretAsync(KeyVaultSecret secret, CancellationToken cancellationToken) =>
    client.SetSecretAsync(secret, cancellationToken);

  public async Task DisableSecretAsync(string name, CancellationToken cancellationToken)
  {
    // An update names a version, and a get returns the current one with its version.
    KeyVaultSecret current = await client.GetSecretAsync(
      name,
      cancellationToken: cancellationToken
    );
    current.Properties.Enabled = false;
    await client.UpdateSecretPropertiesAsync(current.Properties, cancellationToken);
  }

  public Task StartDeleteSecretAsync(string name, CancellationToken cancellationToken) =>
    client.StartDeleteSecretAsync(name, cancellationToken);

  public async Task RecoverDeletedSecretAsync(string name, CancellationToken cancellationToken)
  {
    var operation = await client.StartRecoverDeletedSecretAsync(name, cancellationToken);
    await operation.WaitForCompletionAsync(cancellationToken);
  }
}
