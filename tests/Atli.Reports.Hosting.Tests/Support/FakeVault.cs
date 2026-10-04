using System.Runtime.CompilerServices;
using Atli.Reports.Hosting.Renderers;
using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Atli.Reports.Hosting.Tests.Support;

/// <summary>
/// An in-memory vault with soft delete, answering as Key Vault does (observed 2026-10-04): a missing
/// secret is <c>404</c>, a disabled one <c>403</c> with <c>SecretDisabled</c> in the message, and a
/// deleted name <c>409</c> while it is being deleted (<c>ObjectIsBeingDeleted</c>) and after
/// (<c>ObjectIsDeletedButRecoverable</c>) until it is recovered.
/// </summary>
internal sealed class FakeVault : IRendererSecrets
{
  private readonly Lock _lock = new();
  private readonly Dictionary<string, Secret> _secrets = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<string> _calls = [];

  /// <summary>
  /// How many calls on a secret it takes to finish deleting it; until then, writing or recovering it
  /// fails with <c>ObjectIsBeingDeleted</c>.
  /// </summary>
  public int DeletionCalls { get; set; }

  /// <summary>When set, recovering any secret fails with <c>ObjectIsBeingDeleted</c>, forever.</summary>
  public bool NeverFinishesDeleting { get; set; }

  /// <summary>When set, every read fails with it.</summary>
  public RequestFailedException? ReadFailure { get; set; }

  /// <summary>Each call made, such as <c>Set renderer-acme</c>.</summary>
  public IReadOnlyList<string> Calls
  {
    get
    {
      lock (_lock)
      {
        return [.. _calls];
      }
    }
  }

  /// <summary>Finishes every deletion in progress.</summary>
  public void FinishDeletions()
  {
    lock (_lock)
    {
      foreach (
        var secret in _secrets.Values.Where(secret => secret.State == SecretState.BeingDeleted)
      )
      {
        secret.State = SecretState.Deleted;
      }
    }
  }

  /// <summary>Stores <paramref name="value"/> as an enabled secret, as another writer might.</summary>
  public void Seed(string name, string value, bool enabled = true)
  {
    lock (_lock)
    {
      _secrets[name] = new Secret(value, "application/json", new Dictionary<string, string>())
      {
        Enabled = enabled,
      };
    }
  }

  /// <summary>The secret's current value, content type, and tags, or null.</summary>
  public (string Value, string? ContentType, IReadOnlyDictionary<string, string> Tags)? Peek(
    string name
  )
  {
    lock (_lock)
    {
      return _secrets.TryGetValue(name, out var secret) && secret.State == SecretState.Active
        ? (secret.Value, secret.ContentType, secret.Tags)
        : null;
    }
  }

  public Task<KeyVaultSecret> GetSecretAsync(string name, CancellationToken cancellationToken)
  {
    lock (_lock)
    {
      _calls.Add("Get " + name);
      if (ReadFailure is { } failure)
      {
        throw failure;
      }

      Advance(name);
      if (!_secrets.TryGetValue(name, out var secret) || secret.State != SecretState.Active)
      {
        throw NotFound(name);
      }

      if (!secret.Enabled)
      {
        throw new RequestFailedException(
          403,
          "Operation get is not allowed on a disabled secret.\nStatus: 403 (Forbidden)\n"
            + "ErrorCode: Forbidden\n\nContent:\n"
            + """{"error":{"code":"Forbidden","message":"Operation get is not allowed on a disabled secret.","innererror":{"code":"SecretDisabled"}}}""",
          "Forbidden",
          null
        );
      }

      return Task.FromResult(
        SecretModelFactory.KeyVaultSecret(Properties(name, secret), secret.Value)
      );
    }
  }

  public async IAsyncEnumerable<SecretProperties> GetPropertiesOfSecretsAsync(
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    List<SecretProperties> listed;
    lock (_lock)
    {
      _calls.Add("List");
      listed =
      [
        .. _secrets
          .Where(pair => pair.Value.State == SecretState.Active)
          .Select(pair => Properties(pair.Key, pair.Value)),
      ];
    }

    foreach (var properties in listed)
    {
      await Task.Yield();
      yield return properties;
    }
  }

  public Task SetSecretAsync(KeyVaultSecret secret, CancellationToken cancellationToken)
  {
    lock (_lock)
    {
      _calls.Add("Set " + secret.Name);
      Advance(secret.Name);
      if (_secrets.TryGetValue(secret.Name, out var existing))
      {
        if (existing.State == SecretState.BeingDeleted)
        {
          throw Conflict(secret.Name, "ObjectIsBeingDeleted");
        }

        if (existing.State == SecretState.Deleted)
        {
          throw Conflict(secret.Name, "ObjectIsDeletedButRecoverable");
        }
      }

      _secrets[secret.Name] = new Secret(
        secret.Value,
        secret.Properties.ContentType,
        new Dictionary<string, string>(secret.Properties.Tags)
      );
      return Task.CompletedTask;
    }
  }

  public Task StartDeleteSecretAsync(string name, CancellationToken cancellationToken)
  {
    lock (_lock)
    {
      _calls.Add("Delete " + name);
      Advance(name);
      if (!_secrets.TryGetValue(name, out var secret) || secret.State != SecretState.Active)
      {
        throw NotFound(name);
      }

      secret.State = DeletionCalls > 0 ? SecretState.BeingDeleted : SecretState.Deleted;
      secret.CallsUntilDeleted = DeletionCalls;
      return Task.CompletedTask;
    }
  }

  public Task RecoverDeletedSecretAsync(string name, CancellationToken cancellationToken)
  {
    lock (_lock)
    {
      _calls.Add("Recover " + name);
      Advance(name);
      if (!_secrets.TryGetValue(name, out var secret) || secret.State == SecretState.Active)
      {
        throw NotFound(name);
      }

      if (NeverFinishesDeleting || secret.State == SecretState.BeingDeleted)
      {
        throw Conflict(name, "ObjectIsBeingDeleted");
      }

      secret.State = SecretState.Active;
      return Task.CompletedTask;
    }
  }

  /// <summary>Counts a call toward finishing a deletion in progress.</summary>
  private void Advance(string name)
  {
    if (
      _secrets.TryGetValue(name, out var secret)
      && secret.State == SecretState.BeingDeleted
      && --secret.CallsUntilDeleted <= 0
    )
    {
      secret.State = SecretState.Deleted;
    }
  }

  private static SecretProperties Properties(string name, Secret secret)
  {
    SecretProperties properties = new(name)
    {
      Enabled = secret.Enabled,
      ContentType = secret.ContentType,
    };
    foreach (var tag in secret.Tags)
    {
      properties.Tags[tag.Key] = tag.Value;
    }

    return properties;
  }

  private static RequestFailedException NotFound(string name) =>
    new(
      404,
      $"A secret with (name/id) {name} was not found in this key vault.",
      "SecretNotFound",
      null
    );

  private static RequestFailedException Conflict(string name, string code) =>
    new(409, $"Secret {name} is currently in a deleted state ({code}).", "Conflict", null);

  private enum SecretState
  {
    Active,
    BeingDeleted,
    Deleted,
  }

  private sealed class Secret(
    string value,
    string? contentType,
    IReadOnlyDictionary<string, string> tags
  )
  {
    public string Value { get; } = value;

    public string? ContentType { get; } = contentType;

    public IReadOnlyDictionary<string, string> Tags { get; } = tags;

    public bool Enabled { get; init; } = true;

    public SecretState State { get; set; }

    public int CallsUntilDeleted { get; set; }
  }
}
