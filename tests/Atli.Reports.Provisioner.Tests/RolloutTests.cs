using System.Net;
using Atli.Reports.Hosting.Renderers;
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
        "replaced, but the old sandbox old-a was not deleted (Deleting old-a failed.); "
          + "run rollout or prune again to delete it."
      );
    await Assert.That(provisioning.Records["a"]!.SandboxId).IsEqualTo("sandbox-1");
    // Not tried again by this run's own prune.
    await Assert.That(provisioning.Journal.Matching("delete old-a").Count).IsEqualTo(1);

    // The next run deletes it, as a leftover no record points to.
    provisioning.Sandboxes.FailDelete.Clear();
    var again = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(again.Failures).IsEmpty();
    await Assert.That(again.Pruned).IsEquivalentTo(["old-a"]);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["sandbox-1"]);
  }

  [Test]
  public async Task A_record_write_that_failed_after_writing_still_retires_the_old_sandbox()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.Records.FailPutAfterWriting = new TimeoutException("The answer was lost.");

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Replaced).IsEquivalentTo(["a"]);
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Records["a"]!.SandboxId).IsEqualTo("sandbox-1");
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["sandbox-1"]);
  }

  [Test]
  public async Task A_record_whose_sandbox_is_gone_is_replaced_even_on_the_disk_image()
  {
    using Provisioning provisioning = new();
    var dangling = provisioning.AddRenderer("a", "disk-2", size: "L");
    provisioning.AddRenderer("b", "disk-2");
    provisioning.Sandboxes.Remove("old-a");

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Replaced).IsEquivalentTo(["a"]);
    await Assert.That(result.AlreadyCurrent).IsEqualTo(1);
    await Assert.That(provisioning.Records["a"]!.SandboxId).IsEqualTo("sandbox-1");
    await Assert.That(provisioning.Records["a"]!.ApiKey).IsNotEqualTo(dangling.ApiKey);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-b", "sandbox-1"]);
    await Assert
      .That(provisioning.Output.ToString())
      .Contains("[a] Sandbox old-a of the record no longer exists.");
  }

  [Test]
  public async Task A_tenant_deleted_while_its_replacement_starts_stays_deleted()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    var provisioner = provisioning.Provisioner;
    // An operator's delete runs while the replacement becomes ready.
    provisioning.Readiness.Answer = async (_, cancellationToken) =>
    {
      await provisioner.DeleteAsync("a", TimeSpan.Zero, cancellationToken);
      return ReadinessAnswer.Ready;
    };

    var result = await provisioner.RolloutAsync("disk-2", null, 4, TimeSpan.Zero, TestToken);

    await Assert
      .That(result.Failures["a"])
      .IsEqualTo("The record of tenant a was deleted meanwhile; the replacement is discarded.");
    await Assert.That(provisioning.Records["a"]).IsNull();
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
    await Assert.That(provisioning.Journal.Matching("put")).IsEmpty();
  }

  [Test]
  public async Task A_tenant_another_rollout_replaced_meanwhile_keeps_that_replacement()
  {
    using Provisioning provisioning = new();
    var old = provisioning.AddRenderer("a", "disk-1");
    RendererRecord other = old with { SandboxId = "other-a", DiskImageId = "disk-2" };
    provisioning.Sandboxes.Add("other-a", RendererLabels.For("a", RendererSize.Medium));
    provisioning.Readiness.Answer = (_, _) =>
    {
      provisioning.Records.Add(other);
      return Task.FromResult(ReadinessAnswer.Ready);
    };

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert
      .That(result.Failures["a"])
      .IsEqualTo(
        "The record of tenant a moved to sandbox other-a meanwhile; the replacement is discarded."
      );
    await Assert.That(provisioning.Records["a"]).IsSameReferenceAs(other);
    // The discarded replacement is gone; the other rollout's old sandbox is its to retire.
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-a", "other-a"]);
  }

  [Test]
  public async Task A_canceled_rollout_leaves_the_old_sandbox_and_the_next_run_deletes_it_after_the_drain()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    var drain = TimeSpan.FromSeconds(150);
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
    var rollout = provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      drain,
      cancellation.Token
    );
    await provisioning.Clock.WaitForTimerAsync(drain);
    await cancellation.CancelAsync();
    try
    {
      // Whether the rollout reports the drain as canceled or throws depends on whether it had
      // finished starting replacements; either way the old sandbox stays.
      var canceled = await rollout;
      await Assert
        .That(canceled.Failures["a"])
        .IsEqualTo(
          "replaced, but the old sandbox old-a was not deleted (canceled); "
            + "run rollout or prune again to delete it."
        );
    }
    catch (OperationCanceledException) { }

    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-a", "sandbox-1"]);

    var again = provisioning.Provisioner.RolloutAsync("disk-2", null, 4, drain, TestToken);
    // The record may have moved just before the cancel, so the leftover gets a drain of its own.
    await provisioning.Clock.WaitForTimerAsync(drain);
    await Assert.That(provisioning.Sandboxes.Ids).Contains("old-a");
    provisioning.Clock.Advance(drain);
    var result = await again;

    await Assert.That(result.AlreadyCurrent).IsEqualTo(1);
    await Assert.That(result.Pruned).IsEquivalentTo(["old-a"]);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["sandbox-1"]);
  }

  [Test]
  public async Task A_rollout_of_one_tenant_prunes_only_that_tenants_leftovers()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.AddRenderer("b", "disk-2");
    provisioning.Sandboxes.Add("left-a", RendererLabels.For("a", RendererSize.Medium));
    provisioning.Sandboxes.Add("left-b", RendererLabels.For("b", RendererSize.Medium));

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      "a",
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Pruned).IsEquivalentTo(["left-a"]);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["left-b", "old-b", "sandbox-1"]);
  }

  [Test]
  public async Task An_old_record_naming_another_tenants_sandbox_does_not_delete_it()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("b", "disk-2");
    var tampered = provisioning.AddRenderer("a", "disk-1") with { SandboxId = "old-b" };
    provisioning.Records.Add(tampered);

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      "a",
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert
      .That(result.Failures["a"])
      .IsEqualTo(
        "replaced, but the old record named sandbox old-b, which is labeled for tenant b; "
          + "it was not deleted."
      );
    await Assert.That(provisioning.Sandboxes.Ids).Contains("old-b");
    await Assert.That(provisioning.Records["b"]!.SandboxId).IsEqualTo("old-b");
  }

  [Test]
  public async Task Unreadable_records_are_failures_and_the_others_are_still_replaced()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.Records.AddUnreadable("b");
    // Its sandbox is kept: the record that cannot be read may point to it.
    provisioning.Sandboxes.Add("old-b", RendererLabels.For("b", RendererSize.Medium));

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Replaced).IsEquivalentTo(["a"]);
    await Assert
      .That(result.Failures["b"])
      .IsEqualTo(
        "the record cannot be read (The record of b is damaged.); fix it, or delete the tenant."
      );
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-b", "sandbox-1"]);
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
