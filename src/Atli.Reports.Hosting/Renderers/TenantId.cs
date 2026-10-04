using System.Text.RegularExpressions;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Product tenant identifiers: 1 to 63 lowercase letters, digits, and hyphens, starting with a letter
/// or digit. They name Key Vault secrets and sandbox labels, so nothing else is allowed.
/// </summary>
public static partial class TenantId
{
  /// <summary>Whether <paramref name="value"/> is a valid tenant identifier.</summary>
  public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

  /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="value"/> is valid.</summary>
  public static string Validate(string? value, string parameterName = "tenantId") =>
    IsValid(value)
      ? value!
      : throw new ArgumentException(
        "A tenant ID is 1 to 63 lowercase letters, digits, and hyphens, starting with a letter or digit.",
        parameterName
      );

  // \z, not $: $ also matches before a final newline, and "acme\n" would name another file and secret.
  [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}\\z", RegexOptions.CultureInvariant)]
  private static partial Regex Pattern();
}
