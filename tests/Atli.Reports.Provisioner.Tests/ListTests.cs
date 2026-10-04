using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner.Tests.Support;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>Listing renderers with the state of their sandboxes, and never their credentials.</summary>
public class ListTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Lists_each_renderer_with_its_sandbox_state()
  {
    using Provisioning provisioning = new();
    var running = provisioning.AddRenderer("a", "disk-2");
    var suspended = provisioning.AddRenderer(
      "b",
      "disk-1",
      size: "L",
      state: SandboxStates.Stopped
    );
    var gone = provisioning.AddRenderer("c", "disk-2", size: "S");
    provisioning.Sandboxes.Remove("old-c");

    await provisioning.Provisioner.ListAsync(TestToken);

    var output = provisioning.Output.ToString();
    await Assert
      .That(output)
      .IsEqualTo(
        """
        TENANT  SANDBOX  STATE    SIZE  DISK IMAGE  CREATED
        a       old-a    Running  M     disk-2      2026-09-26 12:00:00Z
        b       old-b    Stopped  L     disk-1      2026-09-26 12:00:00Z
        c       old-c    missing  -     disk-2      2026-09-26 12:00:00Z

        """
      );
    foreach (var record in new[] { running, suspended, gone })
    {
      await Assert.That(output).DoesNotContain(record.ApiKey);
    }
  }

  [Test]
  public async Task Lists_renderer_sandboxes_no_record_points_to()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-2");
    provisioning.Sandboxes.Add(
      "stray",
      RendererLabels.For("a", Hosting.Renderers.RendererSize.Medium),
      SandboxStates.Stopped
    );
    provisioning.Sandboxes.Add("unlabeled", new Dictionary<string, string>());

    await provisioning.Provisioner.ListAsync(TestToken);

    await Assert
      .That(provisioning.Output.ToString())
      .EndsWith(
        """
        Renderer sandboxes no record points to (being created, or left behind by a failed or canceled command):
        SANDBOX  TENANT  STATE
        stray    a       Stopped

        """
      );
    await Assert.That(provisioning.Output.ToString()).DoesNotContain("unlabeled");
  }

  [Test]
  public async Task Lists_the_records_that_cannot_be_read_without_failing()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-2");
    provisioning.Records.AddUnreadable("b");

    await provisioning.Provisioner.ListAsync(TestToken);

    await Assert
      .That(provisioning.Output.ToString())
      .IsEqualTo(
        """
        TENANT  SANDBOX  STATE    SIZE  DISK IMAGE  CREATED
        a       old-a    Running  M     disk-2      2026-09-26 12:00:00Z

        Records that cannot be read (fix them, or delete the tenant):
        TENANT  REASON
        b       The record of b is damaged.

        """
      );
  }

  [Test]
  public async Task Says_when_there_are_no_renderers()
  {
    using Provisioning provisioning = new();

    await provisioning.Provisioner.ListAsync(TestToken);

    await Assert.That(provisioning.Output.ToString()).IsEqualTo("No renderers.\n");
  }
}
