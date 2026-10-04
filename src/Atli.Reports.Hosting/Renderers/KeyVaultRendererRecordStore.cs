using System.Collections.Concurrent;
using System.Net;
using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Records as Key Vault secrets named <c>renderer-&lt;tenant&gt;</c>, each holding the record as JSON. A
/// new secret version replaces a record in one step. The gateway needs only to get and list secrets.
/// </summary>
/// <remarks>
/// <para>
/// A deleted record's secret stays soft-deleted for the vault's retention period, and its name
/// cannot be reused meanwhile. Putting a record for that tenant again recovers the secret first, then
/// writes the new record as its next version.
/// </para>
/// <para>
/// Recovering brings the secret back with its versions as they were, the deleted record current. So
/// a delete disables the current version before it deletes the secret: a recovered secret stays
/// disabled, which gets and lists treat as no record, until the put's write adds an enabled version.
/// A put whose write fails after it recovered the secret deletes the secret again.
/// </para>
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

  /// <summary>How many secret values a list reads at once.</summary>
  internal const int MaxListParallelism = 8;

  /// <summary>
  /// How long a put that recovered a secret, then failed to write it, may take to delete it again.
  /// It runs even when the put was canceled.
  /// </summary>
  internal static readonly TimeSpan RedeleteTimeout = TimeSpan.FromSeconds(30);

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
  public async Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken) =>
    (await ListWithUnreadableAsync(cancellationToken)).Records;

  /// <inheritdoc />
  /// <remarks>
  /// From the secrets' names and properties alone: the enabled secrets named for a valid tenant.
  /// </remarks>
  public async Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken cancellationToken)
  {
    List<string> tenantIds = [];
    await foreach (var properties in _secrets.GetPropertiesOfSecretsAsync(cancellationToken))
    {
      if (
        properties.Enabled is not false
        && properties.Name.StartsWith(SecretPrefix, StringComparison.Ordinal)
        && TenantId.IsValid(properties.Name[SecretPrefix.Length..])
      )
      {
        tenantIds.Add(properties.Name[SecretPrefix.Length..]);
      }
    }

    return [.. tenantIds.Order(StringComparer.Ordinal)];
  }

  /// <inheritdoc />
  /// <remarks>
  /// Reads up to <see cref="MaxListParallelism"/> secrets at once. A secret that does not hold this
  /// tenant's record is reported, not thrown; a vault that refuses a read still fails the list.
  /// </remarks>
  public async Task<RendererRecordListing> ListWithUnreadableAsync(
    CancellationToken cancellationToken
  )
  {
    var tenantIds = await ListTenantIdsAsync(cancellationToken);
    ConcurrentBag<RendererRecord> records = [];
    ConcurrentBag<UnreadableRendererRecord> unreadable = [];
    await Parallel.ForEachAsync(
      tenantIds,
      new ParallelOptions
      {
        MaxDegreeOfParallelism = MaxListParallelism,
        CancellationToken = cancellationToken,
      },
      async (tenantId, token) =>
      {
        try
        {
          // Null when the record was deleted or disabled since the vault was listed.
          if (await GetAsync(tenantId, token) is { } record)
          {
            records.Add(record);
          }
        }
        catch (InvalidDataException exception)
        {
          unreadable.Add(new UnreadableRendererRecord(tenantId, exception.Message));
        }
      }
    );

    return new RendererRecordListing(
      [.. records.OrderBy(record => record.TenantId, StringComparer.Ordinal)],
      [.. unreadable.OrderBy(record => record.TenantId, StringComparer.Ordinal)]
    );
  }

  /// <inheritdoc />
  public async Task PutAsync(RendererRecord record, CancellationToken cancellationToken)
  {
    var json = RendererRecordJson.Serialize(record);
    var name = SecretName(record.TenantId);
    var recovered = false;
    for (var attempt = 1; ; attempt++)
    {
      KeyVaultSecret secret = new(name, json);
      secret.Properties.ContentType = "application/json";
      secret.Properties.Tags["tenant"] = record.TenantId;
      try
      {
        // A new version is enabled, whatever the version before it.
        await _secrets.SetSecretAsync(secret, cancellationToken);
        return;
      }
      catch (RequestFailedException exception)
        when (exception.Status == (int)HttpStatusCode.Conflict
          && attempt < MaxPutAttempts
          && !recovered
        )
      {
        // The tenant's earlier secret is soft-deleted (ObjectIsDeletedButRecoverable), or still
        // being deleted (ObjectIsBeingDeleted), and its name cannot be reused until it is recovered.
      }
      catch (Exception) when (recovered)
      {
        // This put brought the deleted record's secret back. Whether or not that version is
        // disabled, a put that failed must not leave it: delete it again.
        await DeleteAgainAsync(name);
        throw;
      }

      try
      {
        await _secrets.RecoverDeletedSecretAsync(name, cancellationToken);
        // Recovered: the next attempt writes the new record as the secret's next version.
        recovered = true;
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
  /// Disables the secret's current version, then starts the deletion and returns: the secret is
  /// disabled, so no longer a record, at once, and soft-deleted moments later. Works on a secret
  /// whatever it holds, so a damaged or disabled record can always be deleted.
  /// </remarks>
  public async Task DeleteAsync(string tenantId, CancellationToken cancellationToken)
  {
    TenantId.Validate(tenantId);
    await DeleteSecretAsync(SecretName(tenantId), cancellationToken);
  }

  internal static string SecretName(string tenantId) => SecretPrefix + tenantId;

  private async Task DeleteSecretAsync(string name, CancellationToken cancellationToken)
  {
    try
    {
      // Disabled first, so that recovering the deleted secret later does not bring the record back.
      await _secrets.DisableSecretAsync(name, cancellationToken);
    }
    catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
    {
      // No secret, or one already deleted: deleting it succeeds.
      return;
    }
    catch (RequestFailedException exception) when (IsDisabled(exception))
    {
      // Already disabled; delete it all the same.
    }

    try
    {
      await _secrets.StartDeleteSecretAsync(name, cancellationToken);
    }
    catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
    {
      // Deleted meanwhile.
    }
  }

  /// <summary>
  /// Deletes a secret a failed put recovered, even when the put was canceled, for at most
  /// <see cref="RedeleteTimeout"/>. A failure here is swallowed: the put's own failure is the one
  /// to report, and a recovered secret this store deleted is disabled, so no record.
  /// </summary>
  private async Task DeleteAgainAsync(string name)
  {
    using CancellationTokenSource timeout = new(RedeleteTimeout, _time);
    try
    {
      await DeleteSecretAsync(name, timeout.Token);
    }
    catch (Exception)
    {
      // The put's own exception is the one to report.
    }
  }

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
