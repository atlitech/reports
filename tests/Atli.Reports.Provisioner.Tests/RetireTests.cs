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
  public async Task Retires_a_record_whose_sandbox_no_longer_exists()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1");
    provisioning.Sandboxes.Remove("old-app-a");
    // Outside the prefixes, a dangling record is left to rollout, which replaces it.
    provisioning.AddRenderer("contoso", "disk-1");
    provisioning.Sandboxes.Remove("old-contoso");

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert.That(result.Retired).IsEquivalentTo(["app-a"]);
    await Assert.That(provisioning.Records["app-a"]).IsNull();
    await Assert.That(provisioning.Records["contoso"]).IsNotNull();
    await Assert
      .That(provisioning.Output.ToString())
      .StartsWith(
        "[app-a] Retiring: sandbox old-app-a of the record no longer exists.\n"
          + "[app-a] Deleted the record;"
      );
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
    await Assert
      .That(provisioning.Output.ToString())
      .StartsWith(
        "[app-a] Not retired: the record changed since it was read; another command is at work.\n"
      );
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
  public async Task A_record_naming_another_tenants_sandbox_is_not_retired_and_is_reported()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-b", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-b", TimeSpan.FromDays(8));
    var tampered = provisioning.AddRenderer("app-a", "disk-1") with { SandboxId = "old-app-b" };
    provisioning.Records.Add(tampered);

    var result = await provisioning.Provisioner.RetireIdleAsync(Service(), TestToken);

    await Assert
      .That(result.Failures["app-a"])
      .IsEqualTo(
        "the record names sandbox old-app-b, which is labeled for tenant app-b; it was not retired."
      );
    // app-b's own renderer is retired; app-a keeps its record and its sandbox.
    await Assert.That(result.Retired).IsEquivalentTo(["app-b"]);
    await Assert.That(provisioning.Records["app-a"]).IsSameReferenceAs(tampered);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-a"]);
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

  /// <summary>The service's settings: the prefix <c>app-</c>, and a week before retiring by default.</summary>
  private static ProvisioningServiceOptions Service(TimeSpan? retireAfterIdle = null)
  {
    ProvisioningServiceOptions service = new() { RetireAfterIdle = retireAfterIdle ?? Week };
    service.TenantPrefixes.Add(new ManagedTenantPrefix { Prefix = "app-" });
    return service;
  }
}
