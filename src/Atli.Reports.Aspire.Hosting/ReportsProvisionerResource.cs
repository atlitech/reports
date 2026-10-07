namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// The internal service that creates, retires, and deletes tenant renderers in Azure Sandboxes.
/// Configure its sandbox group, record store, disk image, networking, and managed identity before use.
/// </summary>
/// <param name="name">The resource name.</param>
public sealed class ReportsProvisionerResource([ResourceName] string name) : ContainerResource(name)
{
  internal List<string> TenantPrefixes { get; } = [];

  /// <summary>The provisioning service's internal HTTP endpoint.</summary>
  public EndpointReference PrimaryEndpoint => field ??= new(this, "http");
}
