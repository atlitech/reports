using System.Globalization;
using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Provisioner;

/// <summary>
/// <c>Provisioner:Service</c>: the tenant prefixes whose renderers are created on demand and retired
/// when idle, and, for <c>serve</c>, who may call the provisioning service. <c>serve</c>,
/// <c>retire</c>, and <c>rollout --stopped retire</c> use it; the other commands ignore it.
/// </summary>
internal sealed class ProvisioningServiceOptions
{
  public const string SectionName = ProvisionerOptions.SectionName + ":Service";

  /// <summary>The longest <see cref="RetireAfterIdle"/>.</summary>
  public static readonly TimeSpan MaxRetireAfterIdle = TimeSpan.FromDays(365);

  /// <summary>
  /// The gateway's credentials: each an ID and the base64 SHA-256 of the whole <c>id.secret</c> key,
  /// as the server's API keys. Two IDs allow rotation.
  /// </summary>
  public List<ProvisioningServiceApiKey> ApiKeys { get; } = [];

  /// <summary>The prefixes whose tenants the service creates renderers for, each with its quota.</summary>
  public List<ManagedTenantPrefix> TenantPrefixes { get; } = [];

  /// <summary>Renderers the service creates per minute at most, across all prefixes.</summary>
  public int MaxCreatesPerMinute { get; set; } = 60;

  /// <summary>
  /// How long a managed tenant's renderer may stay stopped before it is retired: deleted with its
  /// record, and with them its memory snapshot, to be created again from the current disk image on
  /// the tenant's next conversion. <c>00:00:00</c> never retires.
  /// </summary>
  public TimeSpan RetireAfterIdle { get; set; } = TimeSpan.FromDays(7);

  /// <summary>How often <c>serve</c> looks for renderers to retire.</summary>
  public TimeSpan RetireCheckInterval { get; set; } = TimeSpan.FromHours(1);

  /// <summary>The managed prefix <paramref name="tenantId"/> falls under, or <see langword="null"/>.</summary>
  public ManagedTenantPrefix? PrefixOf(string tenantId) =>
    TenantPrefixes.FirstOrDefault(prefix => TenantPrefix.Owns(prefix.Prefix, tenantId));

  /// <summary>
  /// Throws <see cref="InvalidOperationException"/> naming the first invalid setting. API keys are
  /// required only for <c>serve</c>.
  /// </summary>
  public void Validate(bool requireApiKeys)
  {
    if (TenantPrefixes.Count == 0)
    {
      throw new InvalidOperationException(
        $"{SectionName}:TenantPrefixes is empty: name at least one prefix, such as myapp-."
      );
    }

    for (var i = 0; i < TenantPrefixes.Count; i++)
    {
      var prefix = TenantPrefixes[i];
      if (!TenantPrefix.IsValid(prefix.Prefix))
      {
        throw new InvalidOperationException(
          $"{SectionName}:TenantPrefixes:{i}:Prefix is '{prefix.Prefix}': use 2 to "
            + $"{TenantPrefix.MaxLength} lowercase letters, digits, and hyphens, starting with a "
            + "letter or digit and ending with a hyphen."
        );
      }

      for (var j = 0; j < i; j++)
      {
        if (TenantPrefix.Overlap(prefix.Prefix, TenantPrefixes[j].Prefix))
        {
          throw new InvalidOperationException(
            $"{SectionName}:TenantPrefixes '{TenantPrefixes[j].Prefix}' and '{prefix.Prefix}' "
              + "overlap: one starts with the other."
          );
        }
      }

      if (prefix.MaxTenants is < 1 or > 100_000)
      {
        throw new InvalidOperationException(
          $"{SectionName}:TenantPrefixes:{i}:MaxTenants must be between 1 and 100000."
        );
      }

      if (prefix.Size.Length > 0 && !RendererSizes.TryParse(prefix.Size, out _))
      {
        throw new InvalidOperationException(
          $"{SectionName}:TenantPrefixes:{i}:Size is '{prefix.Size}'; use S, M, or L, or leave it "
            + "empty for Provisioner:Size."
        );
      }
    }

    if (MaxCreatesPerMinute is < 1 or > 10_000)
    {
      throw new InvalidOperationException(
        $"{SectionName}:MaxCreatesPerMinute must be between 1 and 10000."
      );
    }

    if (
      RetireAfterIdle < TimeSpan.Zero
      || (RetireAfterIdle > TimeSpan.Zero && RetireAfterIdle < TimeSpan.FromMinutes(1))
      || RetireAfterIdle > MaxRetireAfterIdle
    )
    {
      throw new InvalidOperationException(
        $"{SectionName}:RetireAfterIdle must be 00:00:00 (never) or between 00:01:00 and "
          + $"{MaxRetireAfterIdle.ToString("c", CultureInfo.InvariantCulture)}."
      );
    }

    if (RetireCheckInterval < TimeSpan.FromMinutes(1) || RetireCheckInterval > TimeSpan.FromDays(1))
    {
      throw new InvalidOperationException(
        $"{SectionName}:RetireCheckInterval must be between 00:01:00 and 1.00:00:00."
      );
    }

    if (!requireApiKeys)
    {
      return;
    }

    if (ApiKeys.Count == 0)
    {
      throw new InvalidOperationException(
        $"{SectionName}:ApiKeys is empty: serve needs the gateway's credential (an Id and the "
          + "base64 SHA-256 Hash of the whole key)."
      );
    }

    HashSet<string> ids = new(StringComparer.Ordinal);
    for (var i = 0; i < ApiKeys.Count; i++)
    {
      var key = ApiKeys[i];
      if (
        string.IsNullOrWhiteSpace(key.Id)
        || key.Id.Length > 128
        || !key.Id.All(c => c is > ' ' and < '\u007f' and not '.')
        || !ids.Add(key.Id)
      )
      {
        throw new InvalidOperationException(
          $"{SectionName}:ApiKeys:{i}:Id must be unique, printable ASCII without spaces or dots."
        );
      }

      if (!IsSha256(key.Hash))
      {
        throw new InvalidOperationException(
          $"{SectionName}:ApiKeys:{i}:Hash must be the base64 SHA-256 of the whole key."
        );
      }
    }
  }

  private static bool IsSha256(string hash)
  {
    Span<byte> bytes = stackalloc byte[33];
    return Convert.TryFromBase64String(hash, bytes, out var written) && written == 32;
  }
}

/// <summary>One of the gateway's credentials for the provisioning service.</summary>
internal sealed class ProvisioningServiceApiKey
{
  /// <summary>The key's ID, the part before the dot.</summary>
  public string Id { get; set; } = "";

  /// <summary>The base64 SHA-256 of the whole <c>id.secret</c> key.</summary>
  public string Hash { get; set; } = "";
}

/// <summary>A tenant prefix whose renderers the service creates on demand.</summary>
internal sealed class ManagedTenantPrefix
{
  /// <summary>The prefix, such as <c>myapp-</c>; see <see cref="TenantPrefix"/>.</summary>
  public string Prefix { get; set; } = "";

  /// <summary>Renderers under the prefix at most; a create beyond it is refused.</summary>
  public int MaxTenants { get; set; } = 1000;

  /// <summary>The size of the prefix's renderers: <c>S</c>, <c>M</c>, <c>L</c>, or empty for <c>Provisioner:Size</c>.</summary>
  public string Size { get; set; } = "";
}
