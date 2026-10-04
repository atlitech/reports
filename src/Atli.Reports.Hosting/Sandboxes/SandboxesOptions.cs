using System.Text.RegularExpressions;

namespace Atli.Reports.Hosting.Sandboxes;

/// <summary>
/// Where a sandbox group's data plane is: the group's subscription, resource group, name, and
/// region. Every data-plane call is scoped to one group.
/// </summary>
public sealed partial class SandboxesOptions
{
  /// <summary>The data-plane API version this client speaks.</summary>
  public const string ApiVersion = "2026-02-01-preview";

  /// <summary>The scope of the data plane's Microsoft Entra tokens.</summary>
  public const string TokenScope = "https://dynamicsessions.io/.default";

  /// <summary>The Azure subscription ID that holds the sandbox group.</summary>
  public string SubscriptionId { get; set; } = "";

  /// <summary>The resource group that holds the sandbox group.</summary>
  public string ResourceGroup { get; set; } = "";

  /// <summary>The sandbox group's name.</summary>
  public string SandboxGroup { get; set; } = "";

  /// <summary>The sandbox group's region, such as <c>eastus2</c>.</summary>
  public string Region { get; set; } = "";

  /// <summary>
  /// The client ID of a user-assigned managed identity to authenticate with; empty for the default
  /// credential chain (a system-assigned identity in Azure, the Azure CLI locally).
  /// </summary>
  public string ManagedIdentityClientId { get; set; } = "";

  /// <summary>
  /// The group's data-plane base address:
  /// <c>https://management.{region}.azuredevcompute.io/subscriptions/{id}/resourceGroups/{rg}/sandboxGroups/{group}</c>.
  /// Throws <see cref="InvalidOperationException"/> as <see cref="Validate"/> does.
  /// </summary>
  /// <remarks>
  /// Every request carries the data plane's bearer token, so the address is built only from checked
  /// settings, each path segment escaped: a region such as <c>x.attacker.example#</c> would otherwise
  /// send the token to another host.
  /// </remarks>
  public Uri GroupUri
  {
    get
    {
      Validate();
      return new Uri(
        $"https://management.{Region}.azuredevcompute.io"
          + $"/subscriptions/{Uri.EscapeDataString(SubscriptionId)}"
          + $"/resourceGroups/{Uri.EscapeDataString(ResourceGroup)}"
          + $"/sandboxGroups/{Uri.EscapeDataString(SandboxGroup)}/"
      );
    }
  }

  /// <summary>
  /// Throws <see cref="InvalidOperationException"/> when a required setting is missing or is not
  /// what Azure allows: the region lowercase letters and digits, the subscription a GUID, and the
  /// resource group and sandbox group names letters, digits, hyphens, underscores, periods, and
  /// parentheses, not ending in a period.
  /// </summary>
  public void Validate()
  {
    if (
      string.IsNullOrWhiteSpace(SubscriptionId)
      || string.IsNullOrWhiteSpace(ResourceGroup)
      || string.IsNullOrWhiteSpace(SandboxGroup)
      || string.IsNullOrWhiteSpace(Region)
    )
    {
      throw new InvalidOperationException(
        "Sandboxes needs SubscriptionId, ResourceGroup, SandboxGroup, and Region."
      );
    }

    // The region becomes part of the host name.
    if (!RegionPattern().IsMatch(Region))
    {
      throw new InvalidOperationException(
        $"Sandboxes Region '{Region}' is not a region name such as eastus2: lowercase letters and digits only."
      );
    }

    // "D" alone: TryParse would also take the {…} and (…) forms.
    if (!Guid.TryParseExact(SubscriptionId, "D", out _))
    {
      throw new InvalidOperationException(
        $"Sandboxes SubscriptionId '{SubscriptionId}' is not a subscription ID such as "
          + "00000000-0000-0000-0000-000000000000."
      );
    }

    CheckName(nameof(ResourceGroup), ResourceGroup);
    CheckName(nameof(SandboxGroup), SandboxGroup);
  }

  private static void CheckName(string setting, string value)
  {
    if (!NamePattern().IsMatch(value) || value.EndsWith('.'))
    {
      throw new InvalidOperationException(
        $"Sandboxes {setting} '{value}' is not an Azure resource name: 1 to 90 letters, digits, "
          + "hyphens, underscores, periods, and parentheses, not ending in a period."
      );
    }
  }

  // \z, not $: $ also matches before a final newline.
  [GeneratedRegex("^[a-z0-9]{1,40}\\z", RegexOptions.CultureInvariant)]
  private static partial Regex RegionPattern();

  // Azure's resource group characters (\w takes Unicode letters and digits). Never /, ?, #, or %,
  // which would change the request's path.
  [GeneratedRegex("^[-\\w.()]{1,90}\\z", RegexOptions.CultureInvariant)]
  private static partial Regex NamePattern();
}
