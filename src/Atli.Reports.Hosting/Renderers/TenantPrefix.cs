using System.Text.RegularExpressions;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Tenant prefixes: a namespace of tenant IDs that one caller owns, such as <c>myapp-</c> for
/// <c>myapp-3f2504e0-4f89-11d3-9a0c-0305e82c3301</c>, so that a multi-tenant application can have a
/// renderer per workspace without configuring each one. A prefix is 2 to 27 lowercase letters,
/// digits, and hyphens, starting with a letter or digit and ending with a hyphen, which leaves room
/// for a 36-character GUID within a tenant ID's 63 characters.
/// </summary>
public static partial class TenantPrefix
{
  /// <summary>The longest prefix: 63 characters of a tenant ID less a 36-character GUID.</summary>
  public const int MaxLength = 27;

  /// <summary>Whether <paramref name="prefix"/> is a valid tenant prefix.</summary>
  public static bool IsValid(string? prefix) => prefix is not null && Pattern().IsMatch(prefix);

  /// <summary>
  /// Whether <paramref name="tenantId"/> is a valid tenant ID under <paramref name="prefix"/>, with at
  /// least one character after it.
  /// </summary>
  public static bool Owns(string prefix, string? tenantId)
  {
    ArgumentNullException.ThrowIfNull(prefix);
    return tenantId is not null
      && tenantId.Length > prefix.Length
      && tenantId.StartsWith(prefix, StringComparison.Ordinal)
      && TenantId.IsValid(tenantId);
  }

  /// <summary>
  /// Whether a tenant ID can fall under both prefixes: one starts with the other, so owners of the
  /// two would share tenants.
  /// </summary>
  public static bool Overlap(string first, string second)
  {
    ArgumentNullException.ThrowIfNull(first);
    ArgumentNullException.ThrowIfNull(second);
    return first.StartsWith(second, StringComparison.Ordinal)
      || second.StartsWith(first, StringComparison.Ordinal);
  }

  // Ends with a hyphen, so that "acme-" cannot own "acmecorp-1"; \z, not $, as in TenantId.
  [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,25}-\\z", RegexOptions.CultureInvariant)]
  private static partial Regex Pattern();
}
