using System.Net;
using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Records as Key Vault secrets named <c>renderer-&lt;tenant&gt;</c>, each holding the record as JSON. A
/// new secret version replaces a record in one step. The gateway needs only to get and list secrets.
/// </summary>
/// <remarks>
/// A deleted record's secret stays soft-deleted for the vault's retention period, and its name
/// cannot be reused meanwhile. Putting a record for that tenant again recovers the secret first, then
/// writes the new record as its next version.
/// </remarks>
public sealed class KeyVaultRendererRecordStore : IRendererRecordStore
{
  /// <summary>The prefix of every record's secret name.</summary>
  internal const string SecretPrefix = "renderer-";

  /// <summary>
  /// How many times a put waits for a soft-deleted secret to finish deleting or recovering before it
  /// gives up. Observed: deletion finishes within about five seconds.
  /// </summary>
  internal const int MaxPutAttempts = 15;

  private readonly IRendererSecrets _secrets;
  private readonly TimeSpan _retryDelay;
  private readonly TimeProvider _time;

  /// <summary>Creates the store.</summary>
  /// <param name="client">The vault's secret client.</param>
  public KeyVaultRendererRecordStore(SecretClient client)
    : this(
      new SecretClientRendererSecrets(client ?? throw new ArgumentNullException(nameof(client))),
      TimeSpan.FromSeconds(2),
      TimeProvider.System
    ) { }

  /// <summary>Creates the store over <paramref name="secrets"/>.</summary>
  /// <param name="secrets">The vault.</param>
  /// <param name="retryDelay">How long a put waits between attempts on a soft-deleted secret.</param>
  /// <param name="time">The clock those waits run on.</param>
  internal KeyVaultRendererRecordStore(
    IRendererSecrets secrets,
    TimeSpan retryDelay,
    TimeProvider time
  )
  {
    _secrets = secrets;
    _retryDelay = retryDelay;
    _time = time;
  }

  /// <inheritdoc />
  /// <remarks>A disabled secret is no record: the vault refuses to read it.</remarks>
  public async Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
  {
    TenantId.Validate(tenantId);
    var name = SecretName(tenantId);
    KeyVaultSecret secret;
    try
    {
      secret = await _secrets.GetSecretAsync(name, cancellationToken);
    }
    catch (RequestFailedException exception)
      when (exception.Status == (int)HttpStatusCode.NotFound || IsDisabled(exception))
    {
      return null;
    }

    return RendererRecordJson.Deserialize(secret.Value, tenantId, $"The Key Vault secret {name}");
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken)
  {
    List<RendererRecord> records = [];
    await foreach (var properties in _secrets.GetPropertiesOfSecretsAsync(cancellationToken))
    {
      if (
        properties.Enabled is false
        || !properties.Name.StartsWith(SecretPrefix, StringComparison.Ordinal)
      )
      {
        continue;
      }

      var tenantId = properties.Name[SecretPrefix.Length..];
      // Null when the record was deleted or disabled since the vault was listed.
      if (TenantId.IsValid(tenantId) && await GetAsync(tenantId, cancellationToken) is { } record)
      {
        records.Add(record);
      }
    }

    return [.. records.OrderBy(record => record.TenantId, StringComparer.Ordinal)];
  }

  /// <inheritdoc />
  public async Task PutAsync(RendererRecord record, CancellationToken cancellationToken)
  {
    var json = RendererRecordJson.Serialize(record);
    var name = SecretName(record.TenantId);
    for (var attempt = 1; ; attempt++)
    {
      KeyVaultSecret secret = new(name, json);
      secret.Properties.ContentType = "application/json";
      secret.Properties.Tags["tenant"] = record.TenantId;
      try
      {
        await _secrets.SetSecretAsync(secret, cancellationToken);
        return;
      }
      catch (RequestFailedException exception)
        when (exception.Status == (int)HttpStatusCode.Conflict && attempt < MaxPutAttempts)
      {
        // The tenant's earlier secret is soft-deleted (ObjectIsDeletedButRecoverable), or still
        // being deleted (ObjectIsBeingDeleted), and its name cannot be reused until it is recovered.
      }

      try
      {
        await _secrets.RecoverDeletedSecretAsync(name, cancellationToken);
        // Recovered: the next attempt writes the new record as the secret's next version.
        continue;
      }
      catch (RequestFailedException exception)
        when (exception.Status is (int)HttpStatusCode.Conflict or (int)HttpStatusCode.NotFound)
      {
        // 409: still being deleted, so it cannot be recovered yet. 404: no longer soft-deleted
        // (recovered or purged meanwhile). Either way, wait and write again.
      }

      await Task.Delay(_retryDelay, _time, cancellationToken);
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// Starts the deletion and returns: the secret is soft-deleted, and gone from gets and lists,
  /// moments later.
  /// </remarks>
  public async Task DeleteAsync(string tenantId, CancellationToken cancellationToken)
  {
    TenantId.Validate(tenantId);
    try
    {
      await _secrets.StartDeleteSecretAsync(SecretName(tenantId), cancellationToken);
    }
    catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
    {
      // No secret, or one already deleted: deleting it succeeds.
    }
  }

  internal static string SecretName(string tenantId) => SecretPrefix + tenantId;

  /// <summary>
  /// The vault refuses to read a disabled secret with <c>403</c> and the inner error code
  /// <c>SecretDisabled</c>, which only the exception's message carries: its
  /// <see cref="RequestFailedException.ErrorCode"/> is the outer <c>Forbidden</c>. Other
  /// <c>403</c>s, such as a missing role, are real failures.
  /// </summary>
  private static bool IsDisabled(RequestFailedException exception) =>
    exception.Status == (int)HttpStatusCode.Forbidden
    && exception.Message.Contains("SecretDisabled", StringComparison.Ordinal);
}
