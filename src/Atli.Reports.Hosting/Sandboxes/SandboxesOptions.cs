namespace Atli.Reports.Hosting.Sandboxes;

/// <summary>
/// Where a sandbox group's data plane is: the group's subscription, resource group, name, and
/// region. Every data-plane call is scoped to one group.
/// </summary>
public sealed class SandboxesOptions
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
  /// </summary>
  public Uri GroupUri =>
    new(
      $"https://management.{Region}.azuredevcompute.io/subscriptions/{SubscriptionId}"
        + $"/resourceGroups/{ResourceGroup}/sandboxGroups/{SandboxGroup}/"
    );

  /// <summary>Throws when a required setting is missing.</summary>
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
  }
}
