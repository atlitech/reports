#pragma warning disable ASPIREPROBES001 // Probes are experimental in Aspire 13.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Atli.Reports.Aspire.Hosting.Tests.Support;

namespace Atli.Reports.Aspire.Hosting.Tests;

public class HostedResourcesTests
{
  [Test]
  public async Task Gateway_runs_without_browser_security_options_or_conversion_commands()
  {
    var gateway = AppModel.CreateBuilder().AddReportsGateway("gateway");
    var environment = await AppModel.PublishedEnvironmentAsync(gateway.Resource);

    await Assert.That(environment["ReportsServer__Mode"]).IsEqualTo("Gateway");
    await Assert
      .That(
        environment.Keys.Where(key => key.StartsWith("ReportsEngine__", StringComparison.Ordinal))
      )
      .IsEmpty();
    await Assert.That(await AppModel.ContainerRuntimeArgumentsAsync(gateway.Resource)).IsEmpty();
    await Assert.That(gateway.Resource.Annotations.OfType<ResourceCommandAnnotation>()).IsEmpty();
    await Assert
      .That(gateway.Resource.Annotations.OfType<OtlpExporterAnnotation>())
      .HasSingleItem();
    await Assert.That(gateway.Resource.TryGetContainerImageName(out var image)).IsTrue();
    await Assert
      .That(image)
      .IsEqualTo($"ghcr.io/atlitech/reports-server:{AppModel.PackageVersion}");
  }

  [Test]
  public async Task Services_have_internal_endpoints_and_readiness_and_liveness_probes()
  {
    var builder = AppModel.CreateBuilder();
    ContainerResource[] resources =
    [
      builder.AddReportsGateway("gateway").Resource,
      builder.AddReportsProvisioner("provisioner").Resource,
    ];
    foreach (var resource in resources)
    {
      var endpoint = resource.Annotations.OfType<EndpointAnnotation>().Single();
      await Assert.That(endpoint.Name).IsEqualTo("http");
      await Assert.That(endpoint.TargetPort).IsEqualTo(8080);
      await Assert.That(endpoint.IsExternal).IsFalse();
      await Assert.That(resource.Annotations.OfType<HealthCheckAnnotation>()).HasSingleItem();
      var probes = resource.Annotations.OfType<EndpointProbeAnnotation>().ToArray();
      await Assert
        .That(probes.Single(probe => probe.Type == ProbeType.Readiness).Path)
        .IsEqualTo("/health/ready");
      await Assert
        .That(probes.Single(probe => probe.Type == ProbeType.Liveness).Path)
        .IsEqualTo("/health/live");
    }
    await Assert.That(resources[1].Annotations.OfType<OtlpExporterAnnotation>()).IsEmpty();
    await Assert.That(await AppModel.ContainerRuntimeArgumentsAsync(resources[1])).IsEmpty();
    await Assert.That(resources[1].TryGetContainerImageName(out var image)).IsTrue();
    await Assert
      .That(image)
      .IsEqualTo($"ghcr.io/atlitech/reports-provisioner:{AppModel.PackageVersion}");
  }

  [Test]
  public async Task Publish_keeps_client_and_service_credentials_as_secret_references_and_uses_tls()
  {
    var builder = AppModel.CreatePublishBuilder();
    var clientKey = builder.AddParameter("client-key", "client.secret-value", secret: true);
    var clientHash = builder.AddParameter("client-hash", "client-hash-value", secret: true);
    var serviceKey = builder.AddParameter("service-key", "gateway.secret-value", secret: true);
    var serviceHash = builder.AddParameter("service-hash", "service-hash-value", secret: true);
    var provisioner = builder
      .AddReportsProvisioner("provisioner")
      .WithApiKeyAuthentication("gateway", serviceHash);
    var gateway = builder
      .AddReportsGateway("gateway")
      .WithApiKeyAuthentication("client", clientKey, clientHash, "app", allowTenantManagement: true)
      .WithProvisioner(provisioner, serviceKey);

    var gatewayEnv = await AppModel.PublishedEnvironmentAsync(gateway.Resource);
    var provisionerEnv = await AppModel.PublishedEnvironmentAsync(provisioner.Resource);
    await Assert
      .That(gatewayEnv["ReportsServer__Authentication__ApiKeys__0__Hash"])
      .IsEqualTo("{client-hash.value}");
    await Assert
      .That(gatewayEnv["ReportsServer__Authentication__ApiKeys__0__CallerId"])
      .IsEqualTo("app");
    await Assert
      .That(gatewayEnv["ReportsServer__Authentication__ApiKeys__0__Permissions__1"])
      .IsEqualTo("reports.tenants");
    await Assert
      .That(gatewayEnv["ReportsServer__Gateway__Provisioning__Mode"])
      .IsEqualTo("OnDemand");
    await Assert
      .That(gatewayEnv["ReportsServer__Gateway__Provisioning__ApiKey"])
      .IsEqualTo("{service-key.value}");
    await Assert
      .That(gatewayEnv["ReportsServer__Gateway__Provisioning__Url"])
      .IsEqualTo("https://{provisioner.bindings.http.host}");
    await Assert
      .That(gatewayEnv.ContainsKey("ReportsServer__Gateway__AllowHttpRenderers"))
      .IsFalse();
    await Assert
      .That(provisionerEnv["Provisioner__Service__ApiKeys__0__Hash"])
      .IsEqualTo("{service-hash.value}");
    await Assert
      .That(string.Join(';', gatewayEnv.Values.Concat(provisionerEnv.Values)))
      .DoesNotContain("secret-value");
    await Assert
      .That(gateway.Resource.ConnectionStringExpression.ValueExpression)
      .IsEqualTo("Endpoint={gateway.bindings.http.url};ApiKey={client-key.value}");
    await Assert
      .That(gateway.Resource.Annotations.OfType<WaitAnnotation>().Single().Resource)
      .IsEqualTo(provisioner.Resource);
  }

  [Test]
  public async Task Gateway_reference_gives_clients_the_full_credential_and_endpoint()
  {
    var builder = AppModel.CreateBuilder();
    var key = builder.AddParameter("key", "client.secret", secret: true);
    var hash = builder.AddParameter("hash", "verifier", secret: true);
    var gateway = builder
      .AddReportsGateway("reports")
      .WithApiKeyAuthentication("client", key, hash);
    AppModel.Allocate(gateway.Resource, "http", 5123);
    var client = builder.AddProject(
      "api",
      "../Api/Api.csproj",
      options => options.ExcludeLaunchProfile = true
    );
    var environment = await AppModel.EnvironmentAddedByAsync(
      client,
      app => app.WithReference(gateway)
    );

    await Assert
      .That(environment["ConnectionStrings__reports"])
      .IsEqualTo("Endpoint=http://localhost:5123;ApiKey=client.secret");
    await Assert.That(environment["REPORTS_APIKEY"]).IsEqualTo("client.secret");
    await Assert.That(environment["REPORTS_URI"]).IsEqualTo("http://localhost:5123");
  }

  [Test]
  public async Task Local_provisioner_reference_uses_the_allocated_endpoint()
  {
    var builder = AppModel.CreateBuilder();
    var serviceKey = builder.AddParameter("service-key", "gateway.secret", secret: true);
    var provisioner = builder.AddReportsProvisioner("provisioner");
    var gateway = builder.AddReportsGateway("gateway");
    AppModel.Allocate(provisioner.Resource, "http", 5130);
    var environment = await AppModel.EnvironmentAddedByAsync(
      gateway,
      resource => resource.WithProvisioner(provisioner, serviceKey)
    );

    await Assert
      .That(environment["ReportsServer__Gateway__Provisioning__Url"])
      .IsEqualTo("http://localhost:5130");
    await Assert.That(environment["ReportsServer__Gateway__AllowHttpRenderers"]).IsEqualTo("true");
  }

  [Test]
  public async Task Gateway_and_provisioner_require_secret_credential_parameters()
  {
    var builder = AppModel.CreateBuilder();
    var plain = builder.AddParameter("plain", "value");
    var secret = builder.AddParameter("secret", "value", secret: true);
    var gateway = builder.AddReportsGateway("gateway");
    var provisioner = builder.AddReportsProvisioner("provisioner");

    await Assert
      .That(() => gateway.WithApiKeyAuthentication("client", plain, secret))
      .Throws<ArgumentException>();
    await Assert
      .That(() => gateway.WithApiKeyAuthentication("client", secret, plain))
      .Throws<ArgumentException>();
    await Assert
      .That(() => provisioner.WithApiKeyAuthentication("gateway", plain))
      .Throws<ArgumentException>();
    await Assert
      .That(() => gateway.WithProvisioner(provisioner, plain))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Tenant_membership_and_provisioning_quotas_use_matching_runtime_sections()
  {
    var builder = AppModel.CreateBuilder();
    var gateway = builder
      .AddReportsGateway("gateway")
      .WithTenantPrefix("app", "workspaces-")
      .WithTenantPrefix("app", "projects-")
      .WithTenantPrefix("other", "other-");
    var provisioner = builder
      .AddReportsProvisioner("provisioner")
      .WithTenantPrefix("workspaces-", maxTenants: 30, maxCreatesPerMinute: 5)
      .WithTenantPrefix("projects-");
    var gatewayEnv = await AppModel.PublishedEnvironmentAsync(gateway.Resource);
    var provisionerEnv = await AppModel.PublishedEnvironmentAsync(provisioner.Resource);

    await Assert.That(gatewayEnv["ReportsServer__Gateway__Tenants__0__CallerId"]).IsEqualTo("app");
    await Assert
      .That(gatewayEnv["ReportsServer__Gateway__Tenants__0__TenantPrefixes__1"])
      .IsEqualTo("projects-");
    await Assert
      .That(gatewayEnv["ReportsServer__Gateway__Tenants__1__CallerId"])
      .IsEqualTo("other");
    await Assert
      .That(provisionerEnv["Provisioner__Service__TenantPrefixes__0__Prefix"])
      .IsEqualTo("workspaces-");
    await Assert
      .That(provisionerEnv["Provisioner__Service__TenantPrefixes__0__MaxTenants"])
      .IsEqualTo("30");
    await Assert
      .That(provisionerEnv["Provisioner__Service__TenantPrefixes__0__MaxCreatesPerMinute"])
      .IsEqualTo("5");
    await Assert
      .That(provisionerEnv["Provisioner__Service__TenantPrefixes__1__MaxTenants"])
      .IsEqualTo("1000");
  }

  [Test]
  [Arguments("readiness-")]
  [Arguments("UPPER-")]
  [Arguments("-invalid-")]
  [Arguments("missing-suffix")]
  [Arguments("a-prefix-that-is-far-too-long-")]
  public async Task Invalid_or_reserved_tenant_prefixes_are_rejected(string prefix)
  {
    var builder = AppModel.CreateBuilder();
    var gateway = builder.AddReportsGateway("gateway");
    var provisioner = builder.AddReportsProvisioner("provisioner");
    await Assert.That(() => gateway.WithTenantPrefix("client", prefix)).Throws<ArgumentException>();
    await Assert.That(() => provisioner.WithTenantPrefix(prefix)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Overlapping_tenant_namespaces_are_rejected()
  {
    var builder = AppModel.CreateBuilder();
    var gateway = builder.AddReportsGateway("gateway").WithTenantPrefix("app", "app-");
    var provisioner = builder.AddReportsProvisioner("provisioner").WithTenantPrefix("app-");
    await Assert
      .That(() => gateway.WithTenantPrefix("other", "app-other-"))
      .Throws<ArgumentException>();
    await Assert.That(() => provisioner.WithTenantPrefix("app-other-")).Throws<ArgumentException>();
    await Assert
      .That(() => provisioner.WithTenantPrefix("other-", maxTenants: 0))
      .Throws<ArgumentOutOfRangeException>();
    await Assert
      .That(() => provisioner.WithTenantPrefix("other-", maxCreatesPerMinute: 10001))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Reconfiguring_authentication_can_revoke_tenant_deletion()
  {
    var builder = AppModel.CreateBuilder();
    var key = builder.AddParameter("key", "client.secret", secret: true);
    var hash = builder.AddParameter("hash", "verifier", secret: true);
    var gateway = builder
      .AddReportsGateway("gateway")
      .WithApiKeyAuthentication("client", key, hash, allowTenantManagement: true)
      .WithApiKeyAuthentication("client", key, hash);
    var environment = await AppModel.PublishedEnvironmentAsync(gateway.Resource);
    await Assert
      .That(environment["ReportsServer__Authentication__ApiKeys__0__Permissions__0"])
      .IsEqualTo("reports.convert");
    await Assert
      .That(environment.ContainsKey("ReportsServer__Authentication__ApiKeys__0__Permissions__1"))
      .IsFalse();
  }
}
