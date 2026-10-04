using Azure.Core;

namespace Atli.Reports.Hosting;

/// <summary>The Microsoft Entra credential the gateway and the provisioner authenticate with.</summary>
public static class AzureCredentials
{
  /// <summary>
  /// A user-assigned managed identity when <paramref name="managedIdentityClientId"/> is set;
  /// otherwise the default chain (a system-assigned managed identity in Azure, the Azure CLI
  /// locally).
  /// </summary>
  public static TokenCredential Create(string? managedIdentityClientId) =>
    throw new NotImplementedException();
}
