using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Provisioner.Tests.Support;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>
/// The kill switch: disabling a tenant's renderer so nothing can start it, and enabling it again.
/// </summary>
public class DisableTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Disables_every_sandbox_of_the_tenant_and_keeps_the_record()
  {
    using Provisioning provisioning = new();
    var record = provisioning.AddRenderer("a", "disk-1");
    provisioning.AddRenderer("b", "disk-1");
    // A leftover of the tenant's, which a compromised renderer may have reached too.
    provisioning.Sandboxes.Add("left-a", RendererLabels.For("a", RendererSize.Medium));

    await provisioning.Provisioner.DisableAsync("a", TestToken);

    await Assert.That(provisioning.Sandboxes.Disabled).IsEquivalentTo(["left-a", "old-a"]);
    await Assert.That(provisioning.Records["a"]).IsSameReferenceAs(record);
    await Assert
      .That(provisioning.Output.ToString())
      .Contains("[a] Disabled sandbox old-a (Stopped).");
  }

  [Test]
  public async Task Enable_lets_the_tenants_sandboxes_start_again()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.AddRenderer("b", "disk-1");
    await provisioning.Provisioner.DisableAsync("a", TestToken);
    await provisioning.Provisioner.DisableAsync("b", TestToken);

    await provisioning.Provisioner.EnableAsync("a", TestToken);

    await Assert.That(provisioning.Sandboxes.Disabled).IsEquivalentTo(["old-b"]);
  }

  [Test]
  public async Task Works_when_the_record_cannot_be_read()
  {
    using Provisioning provisioning = new();
    provisioning.Records.AddUnreadable("a");
    provisioning.Sandboxes.Add("old-a", RendererLabels.For("a", RendererSize.Medium));

    await provisioning.Provisioner.DisableAsync("a", TestToken);

    await Assert.That(provisioning.Sandboxes.Disabled).IsEquivalentTo(["old-a"]);
  }

  [Test]
  public async Task Tries_every_sandbox_then_fails_when_one_could_not_be_disabled()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.Sandboxes.Add("left-a", RendererLabels.For("a", RendererSize.Medium));
    provisioning.Sandboxes.FailDisable.Add("left-a");

    var exception = await Assert
      .That(async () => await provisioning.Provisioner.DisableAsync("a", TestToken))
      .Throws<ProvisioningException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo("Could not disable sandbox left-a: Disabling left-a failed.");
    await Assert.That(provisioning.Sandboxes.Disabled).IsEquivalentTo(["old-a"]);
  }

  [Test]
  public async Task Leaves_a_sandbox_of_another_tenant_the_record_names_alone()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("b", "disk-1");
    provisioning.AddRenderer("a", "disk-1");
    provisioning.Records.Add(provisioning.Records["a"]! with { SandboxId = "old-b" });

    var exception = await Assert
      .That(async () => await provisioning.Provisioner.DisableAsync("a", TestToken))
      .Throws<ProvisioningException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo(
        "The record of tenant a names sandbox old-b, which is labeled for tenant b; it was not disabled."
      );
    await Assert.That(provisioning.Sandboxes.Disabled).IsEquivalentTo(["old-a"]);
  }

  [Test]
  public async Task Says_when_the_tenant_has_no_sandbox()
  {
    using Provisioning provisioning = new();

    await provisioning.Provisioner.DisableAsync("a", TestToken);

    await Assert.That(provisioning.Output.ToString()).IsEqualTo("[a] No sandbox to disable.\n");
  }
}
