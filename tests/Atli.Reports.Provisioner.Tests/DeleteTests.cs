using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner.Tests.Support;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>
/// Deleting a tenant's renderer: the record first, so the gateway stops routing to it, then the
/// sandbox; as often as needed.
/// </summary>
public class DeleteTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Deletes_the_record_before_the_sandbox()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.AddRenderer("b", "disk-1");

    await provisioning.Provisioner.DeleteAsync("a", TimeSpan.Zero, TestToken);

    await Assert
      .That(provisioning.Journal.Matching("delete"))
      .IsEquivalentTo(["delete record a", "delete old-a"], CollectionOrdering.Matching);
    await Assert.That(provisioning.Records["a"]).IsNull();
    await Assert.That(provisioning.Records["b"]).IsNotNull();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-b"]);
  }

  [Test]
  public async Task Waits_the_drain_between_the_record_and_the_sandbox()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    var drain = TimeSpan.FromMinutes(1);

    var delete = provisioning.Provisioner.DeleteAsync("a", drain, TestToken);
    await provisioning.Clock.WaitForTimerAsync(drain);
    await Assert.That(provisioning.Records["a"]).IsNull();
    provisioning.Clock.Advance(drain - TestClock.Tick);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-a"]);
    provisioning.Clock.Advance(TestClock.Tick);
    await delete;

    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
  }

  [Test]
  public async Task Deleting_again_succeeds()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    await provisioning.Provisioner.DeleteAsync("a", TimeSpan.Zero, TestToken);

    await provisioning.Provisioner.DeleteAsync("a", TimeSpan.Zero, TestToken);

    await Assert.That(provisioning.Output.ToString()).EndsWith("[a] No renderer to delete.\n");
  }

  [Test]
  public async Task A_record_that_cannot_be_read_is_deleted_with_the_tenants_sandboxes()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("b", "disk-1");
    provisioning.Records.AddUnreadable("a");
    provisioning.Sandboxes.Add("old-a", RendererLabels.For("a", RendererSize.Medium));

    await provisioning.Provisioner.DeleteAsync("a", TimeSpan.FromMinutes(1), TestToken);

    // No drain: a record that cannot be read routes nothing.
    await Assert.That(provisioning.Records.HasUnreadable("a")).IsFalse();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-b"]);
    await Assert
      .That(provisioning.Output.ToString())
      .StartsWith(
        "[a] The record cannot be read (The record of a is damaged.); deleting it all the same.\n"
      );
  }

  [Test]
  public async Task The_record_is_deleted_even_when_the_store_reads_none()
  {
    using Provisioning provisioning = new();

    await provisioning.Provisioner.DeleteAsync("a", TimeSpan.Zero, TestToken);

    // A Key Vault store reads a disabled record as none, and still holds it.
    await Assert.That(provisioning.Journal.Matching("delete")).IsEquivalentTo(["delete record a"]);
  }

  [Test]
  public async Task A_sandbox_the_record_names_that_is_labeled_for_another_tenant_is_not_deleted()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("b", "disk-1");
    provisioning.AddRenderer("a", "disk-1");
    provisioning.Records.Add(provisioning.Records["a"]! with { SandboxId = "old-b" });

    var exception = await Assert
      .That(async () => await provisioning.Provisioner.DeleteAsync("a", TimeSpan.Zero, TestToken))
      .Throws<ProvisioningException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo(
        "The record of tenant a named sandbox old-b, which is labeled for tenant b; it was not deleted."
      );
    await Assert.That(provisioning.Records["a"]).IsNull();
    // The tenant's own sandbox, found by its label, is deleted all the same.
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-b"]);
  }

  [Test]
  public async Task Deleting_again_removes_a_sandbox_the_first_delete_left_behind()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.AddRenderer("b", "disk-1");
    provisioning.Sandboxes.Add("unlabeled", new Dictionary<string, string>());
    provisioning.Sandboxes.FailDelete.Add("old-a");
    await Assert
      .That(async () => await provisioning.Provisioner.DeleteAsync("a", TimeSpan.Zero, TestToken))
      .Throws<SandboxesException>();
    await Assert.That(provisioning.Records["a"]).IsNull();
    provisioning.Sandboxes.FailDelete.Clear();

    await provisioning.Provisioner.DeleteAsync("a", TimeSpan.Zero, TestToken);

    // Found by its labels, since no record points to it any more.
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-b", "unlabeled"]);
  }
}
