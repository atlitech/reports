using System.Net;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner.Tests.Support;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>
/// Rolling out a disk image: every renderer on another image replaced by a new sandbox with a new
/// credential, the record swapped before the old sandbox goes, and failures contained to their
/// tenant.
/// </summary>
public class RolloutTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Replaces_only_renderers_on_another_disk_image_and_keeps_their_sizes()
  {
    using Provisioning provisioning = new();
    var small = provisioning.AddRenderer("a", "disk-1", size: "S");
    var current = provisioning.AddRenderer("b", "disk-2");
    var large = provisioning.AddRenderer("c", "disk-1", size: "L");

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      tenantId: null,
      maxParallel: 4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Replaced).IsEquivalentTo(["a", "c"]);
    await Assert.That(result.AlreadyCurrent).IsEqualTo(1);
    await Assert.That(result.Failures).IsEmpty();
    var specs = provisioning.Sandboxes.Created.ToDictionary(spec => spec.Labels["tenant"]);
    await Assert.That(specs.Keys).IsEquivalentTo(["a", "c"]);
    await Assert.That(specs["a"].Cpu).IsEqualTo("500m");
    await Assert.That(specs["a"].Labels["size"]).IsEqualTo("S");
    await Assert.That(specs["c"].Cpu).IsEqualTo("2000m");
    await Assert.That(specs["c"].DiskImageId).IsEqualTo("disk-2");
    foreach (var old in new[] { small, large })
    {
      var replacement = provisioning.Records[old.TenantId]!;
      await Assert.That(replacement.DiskImageId).IsEqualTo("disk-2");
      await Assert.That(replacement.SandboxId).IsNotEqualTo(old.SandboxId);
      await Assert.That(replacement.ApiKey).IsNotEqualTo(old.ApiKey);
    }

    await Assert.That(provisioning.Records["b"]).IsSameReferenceAs(current);
    await Assert
      .That(provisioning.Sandboxes.Ids)
      .IsEquivalentTo([
        provisioning.Records["a"]!.SandboxId!,
        "old-b",
        provisioning.Records["c"]!.SandboxId!,
      ]);
  }

  [Test]
  public async Task Swaps_the_record_then_deletes_the_old_sandbox_after_the_drain()
  {
    using Provisioning provisioning = new();
    var old = provisioning.AddRenderer("a", "disk-1");
    var drain = TimeSpan.FromSeconds(150);

    var rollout = provisioning.Provisioner.RolloutAsync("disk-2", null, 4, drain, TestToken);
    await provisioning.Clock.WaitForTimerAsync(drain);
    await Assert.That(provisioning.Records["a"]!.SandboxId).IsEqualTo("sandbox-1");
    provisioning.Clock.Advance(drain - TestClock.Tick);
    await Assert.That(provisioning.Sandboxes.Ids).Contains("old-a");
    provisioning.Clock.Advance(TestClock.Tick);
    var result = await rollout;

    await Assert.That(result.Replaced).IsEquivalentTo(["a"]);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["sandbox-1"]);
    await Assert
      .That(provisioning.Journal.Matching("put", "delete"))
      .IsEquivalentTo(["put a -> sandbox-1", "delete old-a"], CollectionOrdering.Matching);
    await Assert.That(old.SandboxId).IsEqualTo("old-a");
  }

  [Test]
  public async Task Replaces_suspended_renderers_without_resuming_them()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1", state: SandboxStates.Stopped);

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Replaced).IsEquivalentTo(["a"]);
    await Assert.That(provisioning.Journal.Matching("resume")).IsEmpty();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["sandbox-1"]);
  }

  [Test]
  public async Task Creates_at_most_max_parallel_replacements_at_once()
  {
    using Provisioning provisioning = new();
    foreach (var tenant in new[] { "a", "b", "c", "d", "e" })
    {
      provisioning.AddRenderer(tenant, "disk-1");
    }

    var waiting = 0;
    var mostWaiting = 0;
    TaskCompletionSource twoWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    provisioning.Readiness.Answer = async (_, cancellationToken) =>
    {
      var now = Interlocked.Increment(ref waiting);
      InterlockedMax(ref mostWaiting, now);
      if (now == 2)
      {
        twoWaiting.TrySetResult();
      }

      await ready.Task.WaitAsync(cancellationToken);
      Interlocked.Decrement(ref waiting);
      return ReadinessAnswer.Ready;
    };

    var rollout = provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      maxParallel: 2,
      TimeSpan.Zero,
      TestToken
    );
    await twoWaiting.Task.WaitAsync(TestToken);
    // Two replacements wait to be ready; a third would have been created by now if it could be.
    await Task.Delay(TimeSpan.FromMilliseconds(200), TestToken);
    await Assert.That(provisioning.Sandboxes.Created.Count).IsEqualTo(2);
    ready.SetResult();
    var result = await rollout;

    await Assert.That(result.Replaced.Count).IsEqualTo(5);
    await Assert.That(mostWaiting).IsEqualTo(2);
  }

  [Test]
  public async Task A_failed_tenant_keeps_its_renderer_and_the_others_are_still_replaced()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    var failing = provisioning.AddRenderer("b", "disk-1");
    provisioning.AddRenderer("c", "disk-1");
    provisioning.Sandboxes.FailAddPort = sandbox =>
      sandbox.Labels["tenant"] == "b"
        ? new SandboxesException("Too many ports.", HttpStatusCode.Conflict)
        : null;

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      maxParallel: 1,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Replaced).IsEquivalentTo(["a", "c"]);
    await Assert.That(result.Failures.Keys).IsEquivalentTo(["b"]);
    await Assert.That(result.Failures["b"]).IsEqualTo("Too many ports.");
    // b's half-created replacement is gone; its record and sandbox are as they were.
    await Assert.That(provisioning.Records["b"]).IsSameReferenceAs(failing);
    await Assert
      .That(provisioning.Sandboxes.Ids)
      .IsEquivalentTo(["old-b", "sandbox-1", "sandbox-3"]);
    await Assert.That(provisioning.Output.ToString()).Contains("failed 1.\n  b: Too many ports.");
  }

  [Test]
  public async Task Running_again_replaces_only_what_the_last_run_did_not()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.AddRenderer("b", "disk-1");
    provisioning.Sandboxes.FailAddPort = sandbox =>
      sandbox.Labels["tenant"] == "b" ? new SandboxesException("Too many ports.") : null;
    await provisioning.Provisioner.RolloutAsync("disk-2", null, 4, TimeSpan.Zero, TestToken);
    provisioning.Sandboxes.FailAddPort = null;
    var replaced = provisioning.Records["a"];

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Replaced).IsEquivalentTo(["b"]);
    await Assert.That(result.AlreadyCurrent).IsEqualTo(1);
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Records["a"]).IsSameReferenceAs(replaced);
    await Assert.That(provisioning.Records["b"]!.DiskImageId).IsEqualTo("disk-2");

    var third = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(third.Replaced).IsEmpty();
    await Assert.That(third.AlreadyCurrent).IsEqualTo(2);
    await Assert.That(provisioning.Sandboxes.Created.Count).IsEqualTo(3);
  }

  [Test]
  public async Task Replaces_only_the_named_tenant()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    var other = provisioning.AddRenderer("b", "disk-1");

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      "a",
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Replaced).IsEquivalentTo(["a"]);
    await Assert.That(provisioning.Records["b"]).IsSameReferenceAs(other);
  }

  [Test]
  public async Task A_named_tenant_without_a_renderer_is_refused()
  {
    using Provisioning provisioning = new();

    var exception = await Assert
      .That(async () =>
        await provisioning.Provisioner.RolloutAsync("disk-2", "a", 4, TimeSpan.Zero, TestToken)
      )
      .Throws<ProvisioningException>();

    await Assert.That(exception!.Message).IsEqualTo("Tenant a has no renderer; create one.");
  }

  [Test]
  public async Task An_old_sandbox_that_cannot_be_deleted_is_a_failure()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.Sandboxes.FailDelete.Add("old-a");

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Replaced).IsEmpty();
    await Assert
      .That(result.Failures["a"])
      .IsEqualTo(
        "replaced, but the old sandbox old-a was not deleted (Deleting old-a failed.); delete it by hand."
      );
    await Assert.That(provisioning.Records["a"]!.SandboxId).IsEqualTo("sandbox-1");
  }

  private static void InterlockedMax(ref int target, int value)
  {
    var seen = Volatile.Read(ref target);
    while (value > seen)
    {
      var previous = Interlocked.CompareExchange(ref target, value, seen);
      if (previous == seen)
      {
        return;
      }

      seen = previous;
    }
  }
}
