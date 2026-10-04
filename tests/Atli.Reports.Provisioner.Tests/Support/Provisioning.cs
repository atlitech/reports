using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;

namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>
/// A provisioner over in-memory fakes, with the fake clock and the output it writes to.
/// </summary>
internal sealed class Provisioning : IDisposable
{
  public Provisioning()
  {
    Sandboxes = new(Journal, Clock);
    Records = new(Journal);
    Readiness = new(Journal);
  }

  public Journal Journal { get; } = new();

  public FakeSandboxesClient Sandboxes { get; }

  public FakeRecordStore Records { get; }

  public FakeReadinessProbe Readiness { get; }

  public TestClock Clock { get; } = new();

  public StringWriter Output { get; } = new() { NewLine = "\n" };

  public ProvisionerOptions Options { get; } = new();

  /// <summary>The gate every <see cref="Provisioner"/> takes tenants through, as one process.</summary>
  public TenantGate Gate { get; } = new();

  public ProvisionerServices Services => new(Sandboxes, Records, Readiness);

  public RendererProvisioner Provisioner =>
    new(Sandboxes, Records, Readiness, Clock, Output, Options, Gate);

  /// <summary>
  /// A renderer the provisioner created a week ago: sandbox <c>old-{tenant}</c>, labeled as the
  /// provisioner labels renderers, and its record.
  /// </summary>
  public RendererRecord AddRenderer(
    string tenantId,
    string diskImageId,
    string size = "M",
    string state = SandboxStates.Running
  )
  {
    var sandbox = Sandboxes.Add(
      $"old-{tenantId}",
      RendererLabels.For(tenantId, RendererSize.Parse(size)),
      state
    );
    RendererRecord record = new()
    {
      TenantId = tenantId,
      Url = new Uri($"https://{sandbox.Id}-8080.example.test/"),
      ApiKey = RendererCredential.Generate().Credential,
      SandboxId = sandbox.Id,
      DiskImageId = diskImageId,
      CreatedAt = Clock.GetUtcNow() - TimeSpan.FromDays(7),
    };
    Records.Add(record);
    return record;
  }

  public void Dispose() => Output.Dispose();
}
