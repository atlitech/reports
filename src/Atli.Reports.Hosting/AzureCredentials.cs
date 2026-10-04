using Azure.Core;
using Azure.Identity;

namespace Atli.Reports.Hosting;

/// <summary>The Microsoft Entra credential the gateway and the provisioner authenticate with.</summary>
public static class AzureCredentials
{
  /// <summary>
  /// A user-assigned managed identity when <paramref name="managedIdentityClientId"/> is set;
  /// otherwise the default chain (a system-assigned managed identity in Azure, the Azure CLI
  /// locally).
  /// </summary>
  /// <remarks>
  /// Neither ever prompts: a service has nobody to answer a browser sign-in, so the default chain
  /// leaves interactive credentials out and fails instead.
  /// </remarks>
  public static TokenCredential Create(string? managedIdentityClientId) =>
    string.IsNullOrWhiteSpace(managedIdentityClientId)
      ? new DefaultAzureCredential(
        new DefaultAzureCredentialOptions { ExcludeInteractiveBrowserCredential = true }
      )
      : new ManagedIdentityCredential(
        ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId.Trim())
      );
}
