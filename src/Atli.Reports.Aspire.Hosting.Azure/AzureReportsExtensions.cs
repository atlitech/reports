// Aspire 13.6 build options enforce Azure's Linux/amd64 image requirement.
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Publishing;
using Azure.Provisioning.AppContainers;

namespace Aspire.Hosting;

/// <summary>Deploys the Reports hosted-renderer architecture through Aspire.</summary>
public static class AzureReportsExtensions
{
  /// <summary>
  /// Adds separate application and renderer networks, fixed application egress, a renderer sandbox
  /// group without workload identity, a record vault, scoped identities and a Container Apps environment.
  /// </summary>
  /// <remarks>Call only in publish mode; local runs should use the Reports server integration.</remarks>
  public static IResourceBuilder<AzureReportsEnvironmentResource> AddAzureReportsEnvironment(
    this IDistributedApplicationBuilder builder,
    string name
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    if (!builder.ExecutionContext.IsPublishMode)
    {
      throw new InvalidOperationException(
        "AddAzureReportsEnvironment is a deployment resource. Use AddReportsServer during local runs."
      );
    }

    builder.AddAzureProvisioning();
    var gatewayIdentity = builder.AddAzureUserAssignedIdentity($"{name}-gateway-identity");
    var provisionerIdentity = builder.AddAzureUserAssignedIdentity($"{name}-provisioner-identity");
    var apps = builder.AddAzureContainerAppEnvironment($"{name}-apps");
    var resource = new AzureReportsEnvironmentResource(
      name,
      apps,
      gatewayIdentity,
      provisionerIdentity
    );
    var environment = builder
      .AddResource(resource)
      .WithParameter("name", name)
      .WithParameter("gatewayPrincipalId", gatewayIdentity.Resource.PrincipalId)
      .WithParameter("provisionerPrincipalId", provisionerIdentity.Resource.PrincipalId)
      .WithParameter("userPrincipalId")
      .WithParameter("principalType");

    apps.ConfigureInfrastructure(infrastructure =>
    {
      var managedEnvironment = infrastructure
        .GetProvisionableResources()
        .OfType<ContainerAppManagedEnvironment>()
        .Single();
      managedEnvironment.VnetConfiguration = new ContainerAppVnetConfiguration
      {
        InfrastructureSubnetId = resource.ApplicationSubnetId.AsProvisioningParameter(
          infrastructure
        ),
        IsInternal = false,
      };
      managedEnvironment.WorkloadProfiles =
      [
        new ContainerAppWorkloadProfile
        {
          Name = "Consumption",
          WorkloadProfileType = "Consumption",
        },
      ];
    });
    return environment;
  }

  /// <summary>Deploys the gateway with read-only records, its own identity and internal HTTPS ingress.</summary>
  /// <remarks>Call WithExternalHttpEndpoints on the gateway explicitly if public ingress is required.</remarks>
  public static IResourceBuilder<AzureReportsEnvironmentResource> WithReportsGateway(
    this IResourceBuilder<AzureReportsEnvironmentResource> environment,
    IResourceBuilder<ReportsGatewayResource> gateway
  )
  {
    ArgumentNullException.ThrowIfNull(environment);
    ArgumentNullException.ThrowIfNull(gateway);
    var resource = environment.Resource;
    gateway
      .WithComputeEnvironment(resource.ContainerAppEnvironment)
      .WithContainerBuildOptions(options =>
        options.TargetPlatform = ContainerTargetPlatform.LinuxAmd64
      )
      .WithAzureUserAssignedIdentity(resource.GatewayIdentity)
      .WithEnvironment("ReportsServer__Gateway__Records__Store", "KeyVault")
      .WithEnvironment("ReportsServer__Gateway__Records__VaultUri", resource.VaultUri)
      .WithEnvironment(
        "ReportsServer__Gateway__Records__ManagedIdentityClientId",
        resource.GatewayIdentity.Resource.ClientId
      )
      .PublishAsAzureContainerApp((_, app) => ConfigureApp(app, provisioner: false));
    return environment;
  }

  /// <summary>Deploys one provisioning service with sandbox and record management privileges.</summary>
  /// <param name="environment">The hosted Reports infrastructure.</param>
  /// <param name="provisioner">The provisioning service.</param>
  /// <param name="diskImageId">A deployment-time disk-image reference implementing IValueProvider.</param>
  public static IResourceBuilder<AzureReportsEnvironmentResource> WithReportsProvisioner(
    this IResourceBuilder<AzureReportsEnvironmentResource> environment,
    IResourceBuilder<ReportsProvisionerResource> provisioner,
    IManifestExpressionProvider diskImageId
  )
  {
    ArgumentNullException.ThrowIfNull(environment);
    ArgumentNullException.ThrowIfNull(provisioner);
    ArgumentNullException.ThrowIfNull(diskImageId);
    if (diskImageId is not IValueProvider)
    {
      throw new ArgumentException(
        "The disk image must resolve a deployment value.",
        nameof(diskImageId)
      );
    }
    var resource = environment.Resource;
    provisioner
      .WithComputeEnvironment(resource.ContainerAppEnvironment)
      .WithContainerBuildOptions(options =>
        options.TargetPlatform = ContainerTargetPlatform.LinuxAmd64
      )
      .WithAzureUserAssignedIdentity(resource.ProvisionerIdentity)
      .WithEnvironment("Provisioner__Sandboxes__SubscriptionId", resource.SubscriptionId)
      .WithEnvironment("Provisioner__Sandboxes__ResourceGroup", resource.ResourceGroupName)
      .WithEnvironment("Provisioner__Sandboxes__SandboxGroup", resource.SandboxGroupName)
      .WithEnvironment("Provisioner__Sandboxes__Region", resource.Location)
      .WithEnvironment(
        "Provisioner__Sandboxes__ManagedIdentityClientId",
        resource.ProvisionerIdentity.Resource.ClientId
      )
      .WithEnvironment("Provisioner__Records__Store", "KeyVault")
      .WithEnvironment("Provisioner__Records__VaultUri", resource.VaultUri)
      .WithEnvironment(
        "Provisioner__Records__ManagedIdentityClientId",
        resource.ProvisionerIdentity.Resource.ClientId
      )
      .WithEnvironment(context =>
        context.EnvironmentVariables["Provisioner__DiskImageId"] = diskImageId
      )
      .WithEnvironment("Provisioner__NetworkConnection", "renderers")
      .WithEnvironment(
        "Provisioner__AllowedSourceCidrs__0",
        ReferenceExpression.Create($"{resource.NatAddress}/32")
      )
      .WithEnvironment("Provisioner__Size", "S")
      .WithEnvironment("Provisioner__AutoSuspendAfter", "00:01:00")
      .PublishAsAzureContainerApp((_, app) => ConfigureApp(app, provisioner: true));
    return environment;
  }

  private static void ConfigureApp(ContainerApp app, bool provisioner)
  {
    app.Configuration.ActiveRevisionsMode = ContainerAppActiveRevisionsMode.Single;
    app.Configuration.Ingress.AllowInsecure = false;
    if (provisioner)
    {
      app.Configuration.Ingress.External = false;
    }
    app.Template.Scale.MinReplicas = 1;
    app.Template.Scale.MaxReplicas = 1;
    app.Template.TerminationGracePeriodSeconds = 150;
    var container =
      app.Template.Containers[0].Value
      ?? throw new InvalidOperationException(
        "The Reports Container App must have an application container."
      );
    container.Resources = new AppContainerResources
    {
      Cpu = provisioner ? 0.5 : 1.0,
      Memory = provisioner ? "1Gi" : "2Gi",
    };
    container.Probes.Clear();
    container.Probes.Add(Probe(ContainerAppProbeType.Liveness, "/health/live", 10, 3));
    container.Probes.Add(Probe(ContainerAppProbeType.Readiness, "/health/ready", 5, 10));
  }

  private static ContainerAppProbe Probe(
    ContainerAppProbeType type,
    string path,
    int period,
    int timeout
  ) =>
    new()
    {
      ProbeType = type,
      HttpGet = new ContainerAppHttpRequestInfo { Path = path, Port = 8080 },
      InitialDelaySeconds = 2,
      PeriodSeconds = period,
      TimeoutSeconds = timeout,
      FailureThreshold = 3,
    };
}
