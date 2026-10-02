#pragma warning disable ASPIREPROBES001 // Probes are experimental in Aspire 13.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Atli.Reports.Aspire.Hosting.Tests.Support;

namespace Atli.Reports.Aspire.Hosting.Tests;

/// <summary>
/// The probes that deployment targets run against the server.
/// </summary>
public class HealthProbeTests
{
  [Test]
  public async Task Probes_readiness_on_health_ready()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    var probe = Probe(server, ProbeType.Readiness);
    await Assert.That(probe.Path).IsEqualTo("/health/ready");
    await Assert.That(probe.EndpointReference.EndpointName).IsEqualTo("http");
    await Assert.That(probe.PeriodSeconds).IsEqualTo(5);
    await Assert.That(probe.TimeoutSeconds).IsEqualTo(3);
    await Assert.That(probe.FailureThreshold).IsEqualTo(3);
  }

  [Test]
  public async Task Probes_liveness_on_health_live()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    // About 30 seconds without an answer before a restart, which drops the conversions in flight.
    var probe = Probe(server, ProbeType.Liveness);
    await Assert.That(probe.Path).IsEqualTo("/health/live");
    await Assert.That(probe.EndpointReference.EndpointName).IsEqualTo("http");
    await Assert.That(probe.PeriodSeconds).IsEqualTo(10);
    await Assert.That(probe.TimeoutSeconds).IsEqualTo(5);
    await Assert.That(probe.FailureThreshold).IsEqualTo(3);
  }

  [Test]
  public async Task Has_no_startup_probe()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    await Assert
      .That(Probes(server).Select(probe => probe.Type))
      .IsEquivalentTo([ProbeType.Readiness, ProbeType.Liveness]);
  }

  [Test]
  public async Task WithHttpProbe_replaces_a_probe_of_the_same_type()
  {
    var server = AppModel
      .CreateBuilder()
      .AddReportsServer("reports-server")
      .WithHttpProbe(ProbeType.Liveness, "/alive");

    await Assert.That(Probe(server, ProbeType.Liveness).Path).IsEqualTo("/alive");
    await Assert.That(Probe(server, ProbeType.Readiness).Path).IsEqualTo("/health/ready");
  }

  [Test]
  public async Task Keeps_the_probes_when_the_app_does_not_deploy_to_Kubernetes()
  {
    var builder = AppModel.CreatePublishBuilder();
    var server = builder.AddReportsServer("reports-server");

    await AppModel.RaiseBeforeStartAsync(builder);

    await Assert.That(Probes(server)).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Leaves_the_probes_out_on_Kubernetes()
  {
    // Aspire's Kubernetes publisher writes the probe's scheme in lower case, and Kubernetes then
    // never creates the pod (https://github.com/microsoft/aspire/issues/18271).
    var builder = AppModel.CreatePublishBuilder();
    var server = builder.AddReportsServer("reports-server");
    builder.AddKubernetesEnvironment("k8s");

    await AppModel.RaiseBeforeStartAsync(builder);

    await Assert.That(Probes(server)).IsEmpty();
  }

  [Test]
  public async Task Keeps_the_probes_the_AppHost_adds_on_Kubernetes()
  {
    var builder = AppModel.CreatePublishBuilder();
    builder.AddKubernetesEnvironment("k8s");
    var server = builder
      .AddReportsServer("reports-server")
      .WithHttpProbe(ProbeType.Liveness, "/alive");

    await AppModel.RaiseBeforeStartAsync(builder);

    var probe = Probes(server).Single();
    await Assert.That(probe.Type).IsEqualTo(ProbeType.Liveness);
    await Assert.That(probe.Path).IsEqualTo("/alive");
  }

  private static EndpointProbeAnnotation[] Probes(IResourceBuilder<ReportsServerResource> server) =>
    [.. server.Resource.Annotations.OfType<EndpointProbeAnnotation>()];

  private static EndpointProbeAnnotation Probe(
    IResourceBuilder<ReportsServerResource> server,
    ProbeType type
  ) => Probes(server).Single(probe => probe.Type == type);
}
