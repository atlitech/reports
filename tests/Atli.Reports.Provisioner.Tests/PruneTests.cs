using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner.Tests.Support;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>
/// Deleting renderer sandboxes no record points to, after a drain, and never one that may still be
/// in use or being created.
/// </summary>
public class PruneTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Deletes_renderer_sandboxes_no_record_points_to_after_the_drain()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-2");
    // The old sandbox of a rollout canceled in its drain, and the sandbox of a deleted tenant.
    provisioning.Sandboxes.Add("old-rollout-a", RendererLabels.For("a", RendererSize.Medium));
    provisioning.Sandboxes.Add("gone-tenant", RendererLabels.For("b", RendererSize.Small));
    // Not renderers.
    provisioning.Sandboxes.Add("unlabeled", new Dictionary<string, string>());
    provisioning.Sandboxes.Add(
      "renderer-without-tenant",
      new Dictionary<string, string> { ["app"] = "atli-reports", ["role"] = "renderer" }
    );
    var drain = TimeSpan.FromSeconds(150);

    var prune = provisioning.Provisioner.PruneAsync(null, drain, TestToken);
    await provisioning.Clock.WaitForTimerAsync(drain);
    provisioning.Clock.Advance(drain - TestClock.Tick);
    await Assert.That(provisioning.Journal.Matching("delete")).IsEmpty();
    provisioning.Clock.Advance(TestClock.Tick);
    var result = await prune;

    await Assert.That(result.Deleted).IsEquivalentTo(["gone-tenant", "old-rollout-a"]);
    await Assert.That(result.Failures).IsEmpty();
    await Assert
      .That(provisioning.Sandboxes.Ids)
      .IsEquivalentTo(["old-a", "renderer-without-tenant", "unlabeled"]);
  }

  [Test]
  public async Task Keeps_a_sandbox_a_record_points_to_by_the_end_of_the_drain()
  {
    using Provisioning provisioning = new();
    var record = provisioning.AddRenderer("a", "disk-1");
    provisioning.Sandboxes.Add("new-a", RendererLabels.For("a", RendererSize.Medium));
    var drain = TimeSpan.FromSeconds(30);

    var prune = provisioning.Provisioner.PruneAsync("a", drain, TestToken);
    await provisioning.Clock.WaitForTimerAsync(drain);
    // Another command moves the record meanwhile; the sandbox it left is not this prune's to take.
    provisioning.Records.Add(record with { SandboxId = "new-a" });
    provisioning.Clock.Advance(drain);
    var result = await prune;

    await Assert.That(result.Deleted).IsEmpty();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["new-a", "old-a"]);
  }

  [Test]
  public async Task Keeps_sandboxes_that_may_still_be_being_created()
  {
    using Provisioning provisioning = new();
    provisioning.Options.ReadyTimeout = TimeSpan.FromMinutes(3);
    var drain = TimeSpan.FromSeconds(30);
    // A launch writes its record within the ready timeout of its create.
    provisioning.Sandboxes.Add(
      "launching",
      RendererLabels.For("a", RendererSize.Medium),
      age: drain + TimeSpan.FromMinutes(3) - TestClock.Tick
    );
    provisioning.Sandboxes.Add(
      "old-enough",
      RendererLabels.For("a", RendererSize.Medium),
      age: drain + TimeSpan.FromMinutes(3)
    );

    var prune = provisioning.Provisioner.PruneAsync(null, drain, TestToken);
    await provisioning.Clock.WaitForTimerAsync(drain);
    provisioning.Clock.Advance(drain);
    var result = await prune;

    await Assert.That(result.Deleted).IsEquivalentTo(["old-enough"]);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["launching"]);
    await Assert
      .That(provisioning.Output.ToString())
      .Contains("[a] Kept sandbox launching: it may still be being created");
  }

  [Test]
  public async Task Keeps_the_sandboxes_of_a_tenant_whose_record_cannot_be_read()
  {
    using Provisioning provisioning = new();
    provisioning.Records.AddUnreadable("a");
    provisioning.Sandboxes.Add("old-a", RendererLabels.For("a", RendererSize.Medium));

    var result = await provisioning.Provisioner.PruneAsync(null, TimeSpan.Zero, TestToken);

    await Assert.That(result.Deleted).IsEmpty();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-a"]);
    await Assert
      .That(provisioning.Output.ToString())
      .Contains(
        "[a] Kept sandbox old-a: the tenant's record cannot be read, so it may point to it."
      );
  }

  [Test]
  public async Task Prunes_only_the_named_tenant()
  {
    using Provisioning provisioning = new();
    provisioning.Sandboxes.Add("left-a", RendererLabels.For("a", RendererSize.Medium));
    provisioning.Sandboxes.Add("left-b", RendererLabels.For("b", RendererSize.Medium));

    var result = await provisioning.Provisioner.PruneAsync("b", TimeSpan.Zero, TestToken);

    await Assert.That(result.Deleted).IsEquivalentTo(["left-b"]);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["left-a"]);
  }

  [Test]
  public async Task A_leftover_that_cannot_be_deleted_is_a_failure_and_the_rest_still_go()
  {
    using Provisioning provisioning = new();
    provisioning.Sandboxes.Add("left-a", RendererLabels.For("a", RendererSize.Medium));
    provisioning.Sandboxes.Add("left-b", RendererLabels.For("b", RendererSize.Medium));
    provisioning.Sandboxes.FailDelete.Add("left-a");

    var result = await provisioning.Provisioner.PruneAsync(null, TimeSpan.Zero, TestToken);

    await Assert.That(result.Deleted).IsEquivalentTo(["left-b"]);
    await Assert
      .That(result.Failures["a"])
      .IsEqualTo("the leftover sandbox left-a was not deleted (Deleting left-a failed.).");
    await Assert.That(provisioning.Output.ToString()).Contains("Deleted 1, failed 1.");
  }

  [Test]
  public async Task Says_when_nothing_is_left_over()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.Sandboxes.Add("stopped", new Dictionary<string, string>(), SandboxStates.Stopped);

    var result = await provisioning.Provisioner.PruneAsync(null, TimeSpan.Zero, TestToken);

    await Assert.That(result.Deleted).IsEmpty();
    await Assert
      .That(provisioning.Output.ToString())
      .IsEqualTo("No renderer sandboxes are left over.\nDeleted 0, failed 0.\n");
  }
}
