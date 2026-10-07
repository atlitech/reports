using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>The isolated Azure infrastructure for a Reports gateway and provisioning service.</summary>
public sealed class AzureReportsEnvironmentResource : AzureBicepResource
{
  internal AzureReportsEnvironmentResource(
    string name,
    IResourceBuilder<AzureContainerAppEnvironmentResource> containerAppEnvironment,
    IResourceBuilder<AzureUserAssignedIdentityResource> gatewayIdentity,
    IResourceBuilder<AzureUserAssignedIdentityResource> provisionerIdentity
  )
    : base(name, templateResourceName: "Atli.Reports.Aspire.Hosting.Azure.reports.bicep")
  {
    ContainerAppEnvironment = containerAppEnvironment;
    GatewayIdentity = gatewayIdentity;
    ProvisionerIdentity = provisionerIdentity;
  }

  /// <summary>The Container Apps environment hosting trusted application services.</summary>
  public IResourceBuilder<AzureContainerAppEnvironmentResource> ContainerAppEnvironment { get; }

  /// <summary>The gateway identity, with read-only access to renderer records.</summary>
  public IResourceBuilder<AzureUserAssignedIdentityResource> GatewayIdentity { get; }

  /// <summary>The provisioning identity, with renderer and record management permissions.</summary>
  public IResourceBuilder<AzureUserAssignedIdentityResource> ProvisionerIdentity { get; }

  /// <summary>The deployment subscription.</summary>
  public BicepOutputReference SubscriptionId => new("subscriptionId", this);

  /// <summary>The deployment resource group.</summary>
  public BicepOutputReference ResourceGroupName => new("resourceGroupName", this);

  /// <summary>The deployment region.</summary>
  public BicepOutputReference Location => new("location", this);

  /// <summary>The renderer sandbox group name.</summary>
  public BicepOutputReference SandboxGroupName => new("sandboxGroupName", this);

  /// <summary>The renderer sandbox group's ARM identifier.</summary>
  public BicepOutputReference SandboxGroupId => new("sandboxGroupId", this);

  /// <summary>The application subnet attached to a static NAT gateway.</summary>
  public BicepOutputReference ApplicationSubnetId => new("applicationSubnetId", this);

  /// <summary>The renderer record vault URI.</summary>
  public BicepOutputReference VaultUri => new("vaultUri", this);

  /// <summary>The fixed public IPv4 egress address shared by the gateway and provisioner.</summary>
  public BicepOutputReference NatAddress => new("natAddress", this);
}
