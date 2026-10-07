#pragma warning disable ASPIREPIPELINES001 // Exercise the deployment pipeline without cloud actions.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Aspire.Hosting.Azure.Tests;

public class InfrastructureTests
{
  [Test]
  public async Task Renderers_have_separate_networks_blocked_platform_dns_and_no_workload_identity()
  {
    var builder = CreateBuilder();
    var environment = builder.AddAzureReportsEnvironment("reports");
    var template = environment.Resource.GetBicepTemplateString();

    await Assert.That(template).Contains("destinationAddressPrefix: 'AzurePlatformDNS'");
    await Assert.That(template).Contains("access: 'Deny'");
    await Assert.That(template).Contains("direction: 'Outbound'");
    await Assert.That(template).Contains("dnsServers: ['10.42.0.4']");
    await Assert.That(template).Contains("addressPrefixes: ['10.42.0.0/16']");
    await Assert.That(template).Contains("addressPrefixes: ['10.60.0.0/16']");
    await Assert.That(template).DoesNotContain("virtualNetworkPeerings");
    await Assert.That(template).DoesNotContain("identity:");
    await Assert.That(template).Contains("subnetId: rendererNetwork.properties.subnets[0].id");
  }

  [Test]
  public async Task Application_egress_is_static_and_records_roles_are_scoped_per_identity()
  {
    var environment = CreateBuilder().AddAzureReportsEnvironment("reports");
    var template = environment.Resource.GetBicepTemplateString();

    await Assert.That(template).Contains("publicIPAllocationMethod: 'Static'");
    await Assert.That(template).Contains("publicIpAddresses: [{ id: egressIp.id }]");
    await Assert.That(template).Contains("natGateway: { id: nat.id }");
    await Assert.That(template).Contains("enableRbacAuthorization: true");
    await Assert.That(template).Contains("enablePurgeProtection: true");
    var gatewayRole = ResourceBlock(template, "gatewayRecordsRole");
    await Assert.That(gatewayRole).Contains("scope: records");
    await Assert.That(gatewayRole).Contains("principalId: gatewayPrincipalId");
    await Assert.That(gatewayRole).Contains("4633458b-17de-408a-b874-0445c86b69e6");
    var provisionerRole = ResourceBlock(template, "provisionerRecordsRole");
    await Assert.That(provisionerRole).Contains("scope: records");
    await Assert.That(provisionerRole).Contains("principalId: provisionerPrincipalId");
    await Assert.That(provisionerRole).Contains("b86a8fe4-44ce-4948-aee5-eccb2c155cd7");
    var sandboxRole = ResourceBlock(template, "provisionerSandboxRole");
    await Assert.That(sandboxRole).Contains("scope: sandboxGroup");
    await Assert.That(sandboxRole).Contains("principalId: provisionerPrincipalId");
    await Assert.That(sandboxRole).Contains("roleDefinitionId: sandboxOwnerRole");
    await Assert.That(template).Contains("c24cf47c-5077-412d-a19c-45202126392c");
    await Assert
      .That(environment.Resource.GatewayIdentity.Resource.Name)
      .IsNotEqualTo(environment.Resource.ProvisionerIdentity.Resource.Name);
  }

  [Test]
  public async Task Published_container_apps_have_internal_tls_ingress_one_provisioner_and_health_probes()
  {
    var (builder, _, gateway, provisioner) = CreateModel();
    await using var app = builder.Build();
    await PrepareAsync(app);

    foreach (var resource in new ContainerResource[] { gateway.Resource, provisioner.Resource })
    {
      var deployment =
        resource.GetDeploymentTargetAnnotation()?.DeploymentTarget as AzureContainerAppResource
        ?? throw new InvalidOperationException(
          $"No Container App deployment target for {resource.Name}: {string.Join(',', resource.Annotations.Select(a => a.GetType().Name))}."
        );
      var template = deployment.GetBicepTemplateString();
      await Assert.That(template).Contains("allowInsecure: false");
      await Assert.That(template).Contains("external: false");
      await Assert.That(template).Contains("minReplicas: 1");
      await Assert.That(template).Contains("maxReplicas: 1");
      await Assert.That(template).Contains("terminationGracePeriodSeconds: 150");
      await Assert.That(template).Contains("/health/ready");
      await Assert.That(template).Contains("/health/live");
      await Assert.That(template).Contains("UserAssigned");
      await Assert.That(template).DoesNotContain("NoSandbox");
    }
  }

  [Test]
  public async Task Azure_wiring_uses_identity_and_nat_outputs_and_secret_references()
  {
    var (_, environment, gateway, provisioner) = CreateModel();
    var gatewayEnv = await PublishedEnvironmentAsync(gateway.Resource);
    var provisionerEnv = await PublishedEnvironmentAsync(provisioner.Resource);

    await Assert.That(gatewayEnv["ReportsServer__Gateway__Records__Store"]).IsEqualTo("KeyVault");
    await Assert
      .That(gatewayEnv["ReportsServer__Gateway__Records__VaultUri"])
      .IsEqualTo(environment.Resource.VaultUri.ValueExpression);
    await Assert
      .That(gatewayEnv["ReportsServer__Gateway__Records__ManagedIdentityClientId"])
      .IsEqualTo(environment.Resource.GatewayIdentity.Resource.ClientId.ValueExpression);
    await Assert
      .That(provisionerEnv["Provisioner__Sandboxes__ManagedIdentityClientId"])
      .IsEqualTo(environment.Resource.ProvisionerIdentity.Resource.ClientId.ValueExpression);
    await Assert
      .That(provisionerEnv["Provisioner__AllowedSourceCidrs__0"])
      .IsEqualTo(environment.Resource.NatAddress.ValueExpression + "/32");
    await Assert.That(provisionerEnv["Provisioner__NetworkConnection"]).IsEqualTo("renderers");
    await Assert.That(provisionerEnv["Provisioner__DiskImageId"]).IsEqualTo("{disk-image.value}");
    await Assert
      .That(gatewayEnv["ReportsServer__Gateway__Provisioning__Url"])
      .StartsWith("https://");
    await Assert
      .That(gatewayEnv["ReportsServer__Gateway__Provisioning__ApiKey"])
      .IsEqualTo("{service-key.value}");
    await Assert
      .That(gatewayEnv["ReportsServer__Authentication__ApiKeys__0__Hash"])
      .IsEqualTo("{client-hash.value}");
    await Assert
      .That(provisionerEnv["Provisioner__Service__ApiKeys__0__Hash"])
      .IsEqualTo("{service-hash.value}");
    await Assert
      .That(string.Join(';', gatewayEnv.Values.Concat(provisionerEnv.Values)))
      .DoesNotContain("credential-value");
    await Assert
      .That(gatewayEnv.ContainsKey("ReportsServer__Gateway__AllowHttpRenderers"))
      .IsFalse();
  }

  [Test]
  public async Task Azure_environment_is_rejected_during_local_runs()
  {
    var builder = DistributedApplication.CreateBuilder([]);
    await Assert
      .That(() => builder.AddAzureReportsEnvironment("reports"))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Publish_preserves_unbuilt_image_reference_and_orders_image_before_service_provisioning()
  {
    var (builder, environment, _, provisioner) = CreateModel();
    var image = builder.AddReportsRendererImage("renderer", environment, ".");
    provisioner.WithReportsRendererImage(image);
    await using var app = builder.Build();
    List<PipelineStep> steps = [];
    app.Services.GetRequiredService<IDistributedApplicationPipeline>()
      .AddPipelineConfiguration(context =>
      {
        steps = context.Steps.ToList();
        return Task.CompletedTask;
      });
    await PrepareAsync(app);

    var imageStep = steps.Single(step => step.Name == image.Resource.BuildStepName);
    await Assert.That(imageStep.DependsOnSteps).Contains("provision-reports");
    await Assert.That(imageStep.DependsOnSteps).Contains(WellKnownPipelineSteps.DeployPrereq);
    var serviceStep = steps.Single(step =>
      step.Resource is AzureContainerAppResource target
      && target.TargetResource.Name == provisioner.Resource.Name
      && step.Tags.Contains(WellKnownPipelineTags.ProvisionInfrastructure)
    );
    await Assert.That(serviceStep.DependsOnSteps).Contains(image.Resource.BuildStepName);
    await Assert.That(image.Resource.ResolvedImageId).IsNull();
    var environmentVariables = await PublishedEnvironmentAsync(provisioner.Resource);
    await Assert
      .That(environmentVariables["Provisioner__DiskImageId"])
      .IsEqualTo("{renderer.outputs.imageId}");
  }

  private static IDistributedApplicationBuilder CreateBuilder() =>
    DistributedApplication.CreateBuilder(["--operation", "publish"]);

  private static string ResourceBlock(string template, string name)
  {
    var start = template.IndexOf($"resource {name} ", StringComparison.Ordinal);
    if (start < 0)
    {
      throw new InvalidOperationException($"Missing published resource {name}.");
    }
    var end = template.IndexOf("\nresource ", start, StringComparison.Ordinal);
    return end < 0 ? template[start..] : template[start..end];
  }

  private static (
    IDistributedApplicationBuilder Builder,
    IResourceBuilder<AzureReportsEnvironmentResource> Environment,
    IResourceBuilder<ReportsGatewayResource> Gateway,
    IResourceBuilder<ReportsProvisionerResource> Provisioner
  ) CreateModel()
  {
    var builder = CreateBuilder();
    var clientKey = builder.AddParameter("client-key", "client.credential-value", secret: true);
    var clientHash = builder.AddParameter("client-hash", "client-verifier", secret: true);
    var serviceKey = builder.AddParameter("service-key", "gateway.credential-value", secret: true);
    var serviceHash = builder.AddParameter("service-hash", "service-verifier", secret: true);
    var disk = builder.AddParameter("disk-image", "images/renderer");
    var environment = builder.AddAzureReportsEnvironment("reports");
    var provisioner = builder
      .AddReportsProvisioner("provisioner")
      .WithApiKeyAuthentication("gateway", serviceHash)
      .WithTenantPrefix("app-");
    var gateway = builder
      .AddReportsGateway("gateway")
      .WithApiKeyAuthentication("client", clientKey, clientHash, "app")
      .WithTenantPrefix("app", "app-")
      .WithProvisioner(provisioner, serviceKey);
    environment.WithReportsGateway(gateway).WithReportsProvisioner(provisioner, disk.Resource);
    return (builder, environment, gateway, provisioner);
  }

  private static async Task PrepareAsync(DistributedApplication app)
  {
    var cancellationToken = TestContext.Current!.Execution.CancellationToken;
    var model = app.Services.GetRequiredService<DistributedApplicationModel>();
    var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
    foreach (var resource in model.Resources.ToArray())
    {
      await eventing.PublishAsync(
        new InitializeResourceEvent(
          resource,
          eventing,
          app.Services.GetRequiredService<ResourceLoggerService>(),
          app.Services.GetRequiredService<ResourceNotificationService>(),
          app.Services
        ),
        cancellationToken
      );
    }
    await app
      .Services.GetRequiredService<IDistributedApplicationEventing>()
      .PublishAsync(new BeforeStartEvent(app.Services, model), cancellationToken);
    var options = app.Services.GetRequiredService<IOptions<PipelineOptions>>().Value;
    var output = Path.Combine(
      Path.GetTempPath(),
      "reports-publish-tests-" + Guid.NewGuid().ToString("N")
    );
    options.OutputPath = output;
    try
    {
      var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();
      var context = new PipelineContext(
        model,
        new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
        app.Services,
        NullLogger.Instance,
        cancellationToken
      );
      options.Step = "before-start";
      await pipeline.ExecuteAsync(context);
      options.Step = "publish";
      await pipeline.ExecuteAsync(context);
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, recursive: true);
      }
    }
  }

  private static async Task<Dictionary<string, string?>> PublishedEnvironmentAsync(
    IResource resource
  )
  {
    var context = new EnvironmentCallbackContext(
      new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
      resource,
      cancellationToken: TestContext.Current!.Execution.CancellationToken
    );
    foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
    {
      await annotation.Callback(context);
    }
    return context.EnvironmentVariables.ToDictionary(
      pair => pair.Key,
      pair =>
        pair.Value is IManifestExpressionProvider expression
          ? expression.ValueExpression
          : pair.Value.ToString()
    );
  }
}
