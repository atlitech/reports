using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner.Tests.Support;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>
/// Retiring idle renderers: a managed tenant's renderer stopped for longer than RetireAfterIdle is
/// deleted with its record, as delete does, and everything else is kept, as are renderers that
/// changed between the scan and the delete.
/// </summary>
public class RetireTests
{
  private static readonly TimeSpan Week = TimeSpan.FromDays(7);

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Retires_a_managed_renderer_stopped_for_longer_than_RetireAfterIdle()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1");
    provisioning.AddRenderer("app-b", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-a", TimeSpan.FromDays(8));

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEquivalentTo(["app-a"]);
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Records["app-a"]).IsNull();
    await Assert.That(provisioning.Records["app-b"]).IsNotNull();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-b"]);
    await Assert
      .That(provisioning.Journal.Matching("delete"))
      .IsEquivalentTo(["delete record app-a", "delete old-app-a"], CollectionOrdering.Matching);
    var output = provisioning.Output.ToString();
    await Assert
      .That(output)
      .Contains(
        "[app-a] Retiring: sandbox old-app-a has been stopped since 2026-09-25 12:00:00Z, "
          + "longer than 7.00:00:00.\n"
      );
    await Assert.That(output).EndsWith("Retired 1, failed 0.\n");
  }

  [Test]
  public async Task A_renderer_stopped_for_exactly_RetireAfterIdle_is_kept_until_it_is_longer()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-a", Week);

    var kept = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);
    provisioning.Clock.Advance(TestClock.Tick);
    var retired = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(kept.Retired).IsEmpty();
    await Assert.That(retired.Retired).IsEquivalentTo(["app-a"]);
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
  }

  [Test]
  public async Task Keeps_running_disabled_and_recently_stopped_renderers_and_other_tenants()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-running", "disk-1");
    provisioning.AddRenderer("app-recent", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-recent", Week - TimeSpan.FromMinutes(1));
    provisioning.AddRenderer("app-disabled", "disk-1");
    provisioning.Sandboxes.Suspend(
      "old-app-disabled",
      TimeSpan.FromDays(30),
      SandboxStoppedReasons.Disabled
    );
    // The operator's own tenants, and another application's.
    provisioning.AddRenderer("contoso", "disk-1");
    provisioning.Sandboxes.Suspend("old-contoso", TimeSpan.FromDays(30));
    provisioning.AddRenderer("other-a", "disk-1");
    provisioning.Sandboxes.Suspend("old-other-a", TimeSpan.FromDays(30));
    // The prefix alone is no tenant under it.
    provisioning.AddRenderer("app", "disk-1");
    provisioning.Sandboxes.Suspend("old-app", TimeSpan.FromDays(30));

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Records.Count).IsEqualTo(6);
    await Assert.That(provisioning.Sandboxes.Ids.Count).IsEqualTo(6);
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
    // Nothing to say about tenants kept for the ordinary reasons.
    await Assert.That(provisioning.Output.ToString()).IsEqualTo("Retired 0, failed 0.\n");
  }

  [Test]
  public async Task A_disabled_renderer_is_kept_after_it_is_enabled_until_it_has_been_stopped_long_enough()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1");
    await provisioning.Provisioner.DisableAsync("app-a", TestToken);
    provisioning.Clock.Advance(TimeSpan.FromDays(30));

    var disabled = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);
    await provisioning.Provisioner.EnableAsync("app-a", TestToken);
    var enabled = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(disabled.Retired).IsEmpty();
    // Enabled, it is an ordinary stopped renderer, stopped since it was disabled.
    await Assert.That(enabled.Retired).IsEquivalentTo(["app-a"]);
  }

  [Test]
  public async Task Keeps_a_stopped_renderer_whose_stop_time_is_unknown()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-a", ago: null);

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-a"]);
    await Assert
      .That(provisioning.Output.ToString())
      .StartsWith("[app-a] Not retired: when sandbox old-app-a stopped is unknown.\n");
  }

  [Test]
  public async Task Keeps_a_tenant_whose_record_cannot_be_read()
  {
    using Provisioning provisioning = new();
    provisioning.Records.AddUnreadable("app-a");
    provisioning.Sandboxes.Add("old-app-a", RendererLabels.For("app-a", RendererSize.Medium));
    provisioning.Sandboxes.Suspend("old-app-a", TimeSpan.FromDays(30));

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Records.HasUnreadable("app-a")).IsTrue();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-a"]);
    await Assert
      .That(provisioning.Output.ToString())
      .StartsWith(
        "[app-a] Not retired: the record cannot be read (The record of app-a is damaged.).\n"
      );
  }

  [Test]
  public async Task Leaves_a_record_whose_sandbox_is_gone_for_the_service_to_replace()
  {
    using Provisioning provisioning = new();
    var gone = provisioning.AddRenderer("app-a", "disk-1");
    provisioning.Sandboxes.Remove("old-app-a");

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    // Nothing to retire: the gateway has the provisioning service replace it on its next use.
    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(provisioning.Records["app-a"]).IsSameReferenceAs(gone);
    await Assert.That(provisioning.Output.ToString()).IsEqualTo("Retired 0, failed 0.\n");
  }

  [Test]
  public async Task Keeps_a_renderer_whose_record_another_command_changed_after_the_scan()
  {
    using Provisioning provisioning = new();
    var scanned = provisioning.AddRenderer("app-a", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-a", TimeSpan.FromDays(8));
    // A rollout replaces the renderer once the scan has read the record and the sandboxes.
    RendererRecord replacement = scanned with
    {
      SandboxId = "new-app-a",
      DiskImageId = "disk-2",
    };
    provisioning.Sandboxes.AfterList = () =>
    {
      provisioning.Sandboxes.Add(
        "new-app-a",
        RendererLabels.For("app-a", RendererSize.Medium),
        age: TimeSpan.Zero
      );
      provisioning.Records.Add(replacement);
      return Task.CompletedTask;
    };

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Records["app-a"]).IsSameReferenceAs(replacement);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["new-app-a", "old-app-a"]);
    await Assert.That(provisioning.Journal.Matching("delete")).IsEmpty();
    // The scan's sandbox is a leftover now, which prune deletes.
    await Assert
      .That(provisioning.Output.ToString())
      .StartsWith("[app-a] Not retired: its record does not name sandbox old-app-a, a leftover.\n");
  }

  [Test]
  public async Task Keeps_a_renderer_a_request_wakes_after_the_scan()
  {
    using Provisioning provisioning = new();
    var record = provisioning.AddRenderer("app-a", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-a", TimeSpan.FromDays(8));
    provisioning.Sandboxes.AfterList = () =>
    {
      provisioning.Sandboxes.Wake("old-app-a");
      return Task.CompletedTask;
    };

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Records["app-a"]).IsSameReferenceAs(record);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-a"]);
    await Assert
      .That(provisioning.Output.ToString())
      .StartsWith(
        "[app-a] Not retired: sandbox old-app-a is Running now; a request may be waking it.\n"
      );
  }

  [Test]
  public async Task Keeps_a_renderer_disabled_after_the_scan()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-a", TimeSpan.FromDays(8));
    provisioning.Sandboxes.AfterList = () =>
      provisioning.Sandboxes.DisableAsync("old-app-a", TestToken);

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-a"]);
    await Assert
      .That(provisioning.Output.ToString())
      .StartsWith("[app-a] Not retired: sandbox old-app-a was disabled meanwhile.\n");
  }

  [Test]
  public async Task Does_nothing_when_RetireAfterIdle_is_zero()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-a", TimeSpan.FromDays(365));
    var listed = false;
    provisioning.Sandboxes.AfterList = () =>
    {
      listed = true;
      return Task.CompletedTask;
    };

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(TimeSpan.Zero), TestToken);

    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(listed).IsFalse();
    await Assert.That(provisioning.Records["app-a"]).IsNotNull();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-a"]);
    await Assert
      .That(provisioning.Output.ToString())
      .IsEqualTo("Retiring is off: Provisioner:Service:RetireAfterIdle is 00:00:00.\n");
  }

  [Test]
  public async Task A_sandbox_is_retired_only_for_the_tenant_it_is_labeled_for()
  {
    using Provisioning provisioning = new();
    // app-b's record names its running renderer; old-app-b is a stopped leftover of app-b's.
    provisioning.AddRenderer("app-b", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-b", TimeSpan.FromDays(8));
    var current = provisioning.Records["app-b"]! with { SandboxId = "new-app-b" };
    provisioning.Sandboxes.Add("new-app-b", RendererLabels.For("app-b", RendererSize.Medium));
    provisioning.Records.Add(current);
    // app-a's record names that leftover.
    var tampered = provisioning.AddRenderer("app-a", "disk-1") with
    {
      SandboxId = "old-app-b",
    };
    provisioning.Records.Add(tampered);

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    // Neither: app-b's record does not name it, and it is not app-a's.
    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Records["app-a"]).IsSameReferenceAs(tampered);
    await Assert.That(provisioning.Records["app-b"]).IsSameReferenceAs(current);
    await Assert
      .That(provisioning.Sandboxes.Ids)
      .IsEquivalentTo(["new-app-b", "old-app-a", "old-app-b"]);
  }

  [Test]
  public async Task A_sandbox_that_cannot_be_deleted_is_a_failure_and_the_others_are_still_retired()
  {
    using Provisioning provisioning = new();
    foreach (var tenant in new[] { "app-a", "app-b", "app-c" })
    {
      provisioning.AddRenderer(tenant, "disk-1");
      provisioning.Sandboxes.Suspend($"old-{tenant}", TimeSpan.FromDays(8));
    }

    provisioning.Sandboxes.FailDelete.Add("old-app-b");

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEquivalentTo(["app-a", "app-c"]);
    await Assert.That(result.Failures["app-b"]).IsEqualTo("Deleting old-app-b failed.");
    // Its record is gone, so prune deletes the sandbox left behind.
    await Assert.That(provisioning.Records["app-b"]).IsNull();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-b"]);
    await Assert
      .That(provisioning.Output.ToString())
      .EndsWith("Retired 2, failed 1.\n  app-b: Deleting old-app-b failed.\n");
  }

  [Test]
  public async Task Leaves_a_tenant_whose_renderer_is_being_created_or_deleted()
  {
    using Provisioning provisioning = new();
    foreach (var tenant in new[] { "app-a", "app-b", "app-c" })
    {
      provisioning.AddRenderer(tenant, "disk-1");
      provisioning.Sandboxes.Suspend($"old-{tenant}", TimeSpan.FromDays(8));
    }

    // In the provisioning service, a delete of app-a and a creation for app-b are in flight.
    using var deleting = provisioning.Gate.TryHold("app-a");
    TaskCompletionSource<EnsureResult> creating = new();
    provisioning.Gate.TryStartCreation("app-b", () => creating.Task, out _, out _);

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEquivalentTo(["app-c"]);
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Records["app-a"]).IsNotNull();
    await Assert.That(provisioning.Records["app-b"]).IsNotNull();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-a", "old-app-b"]);
    var output = provisioning.Output.ToString();
    await Assert
      .That(output)
      .Contains("[app-a] Not retired: its renderer is being created or deleted.\n");
    await Assert
      .That(output)
      .Contains("[app-b] Not retired: its renderer is being created or deleted.\n");
    creating.SetCanceled(TestToken);
  }

  [Test]
  public async Task Deletes_only_the_sandboxes_the_run_listed()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-a", TimeSpan.FromDays(8));
    // Made for the tenant once the run has listed the sandboxes.
    provisioning.Sandboxes.AfterList = () =>
    {
      provisioning.Sandboxes.AfterList = null;
      provisioning.Sandboxes.Add(
        "new-app-a",
        RendererLabels.For("app-a", RendererSize.Medium),
        age: TimeSpan.Zero
      );
      return Task.CompletedTask;
    };

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEquivalentTo(["app-a"]);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["new-app-a"]);
  }

  [Test]
  public async Task Retires_under_every_managed_prefix()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-a", TimeSpan.FromDays(8));
    provisioning.AddRenderer("crm-a", "disk-1");
    provisioning.Sandboxes.Suspend("old-crm-a", TimeSpan.FromDays(8));
    var service = Service();
    service.TenantPrefixes.Add(new ManagedTenantPrefix { Prefix = "crm-" });

    var result = await provisioning.Provisioner.RetireIdleAsync(service, TestToken);

    await Assert.That(result.Retired).IsEquivalentTo(["app-a", "crm-a"]);
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
  }

  [Test]
  public async Task Lists_the_sandboxes_once_and_reads_only_the_candidates_records()
  {
    using Provisioning provisioning = new();
    foreach (var tenant in new[] { "app-a", "app-b", "app-c", "app-d", "contoso" })
    {
      provisioning.AddRenderer(tenant, "disk-1");
    }

    provisioning.Sandboxes.Suspend("old-app-a", TimeSpan.FromDays(8));
    provisioning.Sandboxes.Suspend("old-app-b", TimeSpan.FromDays(9));
    provisioning.Sandboxes.Suspend("old-app-d", TimeSpan.FromDays(1));
    provisioning.Sandboxes.Suspend("old-contoso", TimeSpan.FromDays(30));
    var listings = 0;
    provisioning.Sandboxes.AfterList = () =>
    {
      Interlocked.Increment(ref listings);
      return Task.CompletedTask;
    };
    ListingRecordStore records = new(provisioning.Records);
    RendererProvisioner provisioner = new(
      provisioning.Sandboxes,
      records,
      provisioning.Readiness,
      provisioning.Clock,
      provisioning.Output,
      provisioning.Options,
      provisioning.Gate
    );

    var result = await provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEquivalentTo(["app-a", "app-b"]);
    await Assert.That(listings).IsEqualTo(1);
    await Assert.That(records.FullListings).IsEqualTo(0);
    await Assert.That(records.Gets.Distinct()).IsEquivalentTo(["app-a", "app-b"]);
  }

  [Test]
  public async Task Retire_from_the_command_line_deletes_no_leftovers()
  {
    using Provisioning provisioning = new();
    provisioning.Sandboxes.Add("left-app-a", RendererLabels.For("app-a", RendererSize.Medium));

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Pruned).IsEmpty();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["left-app-a"]);
    await Assert.That(provisioning.Output.ToString()).IsEqualTo("Retired 0, failed 0.\n");
  }

  [Test]
  public async Task Deletes_the_leftovers_of_managed_tenants_after_the_drain()
  {
    using Provisioning provisioning = new();
    var labels = (string tenant) => RendererLabels.For(tenant, RendererSize.Medium);
    // Deleted: a leftover no record names, and one beside a tenant's renderer.
    provisioning.Sandboxes.Add("left-app-a", labels("app-a"));
    provisioning.AddRenderer("app-b", "disk-1");
    provisioning.Sandboxes.Add("left-app-b", labels("app-b"));
    // Kept: one that may still be being created, one outside the prefixes, one of a tenant whose
    // record cannot be read, a disabled one, one a record names by the end of the drain, and one
    // whose tenant is being deleted then.
    provisioning.Sandboxes.Add("young-app-c", labels("app-c"), age: TimeSpan.FromMinutes(1));
    provisioning.Sandboxes.Add("left-contoso", labels("contoso"));
    provisioning.Records.AddUnreadable("app-d");
    provisioning.Sandboxes.Add("left-app-d", labels("app-d"));
    provisioning.Sandboxes.Add("left-app-e", labels("app-e"));
    await provisioning.Sandboxes.DisableAsync("left-app-e", TestToken);
    provisioning.Sandboxes.Add("left-app-f", labels("app-f"));
    provisioning.Sandboxes.Add("left-app-g", labels("app-g"));
    var drain = provisioning.Options.DrainDelay;

    var run = provisioning.Provisioner.RetireIdleAsync(Service(), pruneLeftovers: true, TestToken);
    await provisioning.Clock.WaitForTimerAsync(drain);
    provisioning.Records.Add(
      new RendererRecord
      {
        TenantId = "app-f",
        Url = new Uri("https://left-app-f-8080.example.test/"),
        ApiKey = RendererCredential.Generate().Credential,
        SandboxId = "left-app-f",
      }
    );
    using var deleting = provisioning.Gate.TryHold("app-g");
    provisioning.Clock.Advance(drain);
    var result = await run;

    await Assert.That(result.Pruned).IsEquivalentTo(["left-app-a", "left-app-b"]);
    await Assert.That(result.Failures).IsEmpty();
    await Assert
      .That(provisioning.Sandboxes.Ids)
      .IsEquivalentTo([
        "left-app-d",
        "left-app-e",
        "left-app-f",
        "left-app-g",
        "left-contoso",
        "old-app-b",
        "young-app-c",
      ]);
    var output = provisioning.Output.ToString();
    await Assert
      .That(output)
      .Contains(
        "Deleting 4 leftover renderer sandbox(es) in 00:02:30, unless a record points to them by "
          + "then.\n"
      );
    await Assert
      .That(output)
      .Contains("[app-f] Kept sandbox left-app-f: a record may point to it now.\n");
    await Assert
      .That(output)
      .Contains("[app-g] Kept sandbox left-app-g: the tenant is being created or deleted.\n");
    await Assert.That(output).EndsWith("Retired 0, deleted 2 leftover sandbox(es), failed 0.\n");
  }

  [Test]
  public async Task A_young_sandbox_is_not_a_leftover_until_the_drain_and_ready_timeout_have_passed()
  {
    using Provisioning provisioning = new();
    var age = provisioning.Options.DrainDelay + provisioning.Options.ReadyTimeout;
    provisioning.Sandboxes.Add(
      "left-app-a",
      RendererLabels.For("app-a", RendererSize.Medium),
      age: age - TestClock.Tick
    );

    var young = await provisioning.Provisioner.RetireIdleAsync(
      Service(),
      pruneLeftovers: true,
      TestToken
    );

    await Assert.That(young.Pruned).IsEmpty();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["left-app-a"]);
  }

  /// <summary>The service's settings: the prefix <c>app-</c>, and a week before retiring by default.</summary>
  private static ProvisioningServiceOptions Service(TimeSpan? retireAfterIdle = null)
  {
    ProvisioningServiceOptions service = new() { RetireAfterIdle = retireAfterIdle ?? Week };
    service.TenantPrefixes.Add(new ManagedTenantPrefix { Prefix = "app-" });
    return service;
  }
}
