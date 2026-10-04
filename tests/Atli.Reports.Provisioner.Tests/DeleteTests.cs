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
