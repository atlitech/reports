using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner.Tests.Support;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>
/// A rollout that retires stopped renderers (<c>--stopped retire</c>): a managed tenant's stopped
/// renderer is deleted with its record instead of replaced, to come back on its next conversion,
/// and every other renderer is replaced or skipped as without it.
/// </summary>
public class RolloutRetireTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Retires_managed_stopped_renderers_and_replaces_the_others()
  {
    using Provisioning provisioning = new();
    AddMix(provisioning);

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      tenantId: null,
      maxParallel: 4,
      TimeSpan.Zero,
      Service(),
      TestToken
    );

    await Assert.That(result.Retired).IsEquivalentTo(["app-stopped", "app-suspended"]);
    await Assert
      .That(result.Replaced)
      .IsEquivalentTo(["app-disabled", "app-gone", "app-running", "contoso"]);
    await Assert.That(result.AlreadyCurrent).IsEqualTo(1);
    await Assert.That(result.Failures).IsEmpty();
    await Assert.That(provisioning.Records["app-stopped"]).IsNull();
    await Assert.That(provisioning.Records["app-suspended"]).IsNull();
    await Assert.That(provisioning.Records["app-current"]!.SandboxId).IsEqualTo("old-app-current");
    foreach (var tenant in result.Replaced)
    {
      await Assert.That(provisioning.Records[tenant]!.DiskImageId).IsEqualTo("disk-2");
    }

    // Only the replaced tenants got new sandboxes; the retired ones' are gone.
    await Assert
      .That(provisioning.Sandboxes.Created.Select(spec => spec.Labels["tenant"]))
      .IsEquivalentTo(["app-disabled", "app-gone", "app-running", "contoso"]);
    await Assert.That(provisioning.Sandboxes.Ids).DoesNotContain("old-app-stopped");
    await Assert.That(provisioning.Sandboxes.Ids).DoesNotContain("old-app-suspended");
    var output = provisioning.Output.ToString();
    await Assert
      .That(output)
      .Contains(
        "Rollout of disk image disk-2: 4 to replace, 2 stopped to retire, 1 already on it."
      );
    await Assert
      .That(output)
      .Contains(
        "[app-stopped] Retiring the stopped sandbox old-app-stopped; the tenant's next "
          + "conversion creates a new renderer.\n"
      );
    await Assert
      .That(output)
      .EndsWith(
        "Replaced 4, retired 2, already on the image 1, failed 0.\n"
          + "Retired, to be created again on their next conversion:\n"
          + "  app-stopped\n"
          + "  app-suspended\n"
      );
  }

  [Test]
  public async Task Without_retiring_stopped_renderers_are_replaced_as_before()
  {
    using Provisioning provisioning = new();
    AddMix(provisioning);

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      TestToken
    );

    await Assert.That(result.Retired).IsEmpty();
    await Assert
      .That(result.Replaced)
      .IsEquivalentTo([
        "app-disabled",
        "app-gone",
        "app-running",
        "app-stopped",
        "app-suspended",
        "contoso",
      ]);
    await Assert.That(provisioning.Records.Count).IsEqualTo(7);
    var output = provisioning.Output.ToString();
    await Assert
      .That(output)
      .Contains("Rollout of disk image disk-2: 6 to replace, 1 already on it.");
    await Assert.That(output).EndsWith("Replaced 6, already on the image 1, failed 0.\n");
    await Assert.That(output).DoesNotContain("retire");
    await Assert.That(output).DoesNotContain("Retir");
  }

  [Test]
  public async Task A_stopped_renderer_that_starts_before_it_is_retired_is_replaced()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1", state: SandboxStates.Stopped);
    // A request wakes it once the rollout has listed the sandboxes.
    provisioning.Sandboxes.AfterList = () =>
    {
      provisioning.Sandboxes.AfterList = null;
      provisioning.Sandboxes.Wake("old-app-a");
      return Task.CompletedTask;
    };

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      Service(),
      TestToken
    );

    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(result.Replaced).IsEquivalentTo(["app-a"]);
    await Assert.That(provisioning.Records["app-a"]!.SandboxId).IsEqualTo("sandbox-1");
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["sandbox-1"]);
    await Assert
      .That(provisioning.Output.ToString())
      .Contains(
        "[app-a] Not retired: sandbox old-app-a is Running now; a request may be waking it.\n"
          + "[app-a] Replacing sandbox old-app-a"
      );
  }

  [Test]
  public async Task A_stopped_renderer_whose_record_changed_meanwhile_is_a_failure()
  {
    using Provisioning provisioning = new();
    var old = provisioning.AddRenderer("app-a", "disk-1", state: SandboxStates.Stopped);
    RendererRecord other = old with { SandboxId = "other-app-a", DiskImageId = "disk-2" };
    provisioning.Sandboxes.AfterList = () =>
    {
      provisioning.Sandboxes.AfterList = null;
      provisioning.Sandboxes.Add(
        "other-app-a",
        RendererLabels.For("app-a", RendererSize.Medium),
        age: TimeSpan.Zero
      );
      provisioning.Records.Add(other);
      return Task.CompletedTask;
    };

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      Service(),
      TestToken
    );

    await Assert.That(result.Retired).IsEmpty();
    await Assert
      .That(result.Failures["app-a"])
      .IsEqualTo("the record changed since the rollout read it; run rollout again.");
    await Assert.That(provisioning.Records["app-a"]).IsSameReferenceAs(other);
    await Assert.That(provisioning.Sandboxes.Created).IsEmpty();
  }

  [Test]
  public async Task A_named_tenant_is_retired_by_the_same_rule()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1", state: SandboxStates.Stopped);
    provisioning.AddRenderer("app-b", "disk-1", state: SandboxStates.Stopped);
    provisioning.AddRenderer("contoso", "disk-1", state: SandboxStates.Stopped);

    var retired = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      "app-a",
      4,
      TimeSpan.Zero,
      Service(),
      TestToken
    );
    var replaced = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      "contoso",
      4,
      TimeSpan.Zero,
      Service(),
      TestToken
    );

    await Assert.That(retired.Retired).IsEquivalentTo(["app-a"]);
    await Assert.That(retired.Replaced).IsEmpty();
    await Assert.That(replaced.Retired).IsEmpty();
    await Assert.That(replaced.Replaced).IsEquivalentTo(["contoso"]);
    await Assert.That(provisioning.Records["app-a"]).IsNull();
    await Assert.That(provisioning.Records["app-b"]!.SandboxId).IsEqualTo("old-app-b");
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-b", "sandbox-1"]);
  }

  [Test]
  public async Task A_retirement_that_fails_is_a_failure_and_the_others_carry_on()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1", state: SandboxStates.Stopped);
    provisioning.AddRenderer("app-b", "disk-1");
    provisioning.Sandboxes.FailDelete.Add("old-app-a");

    var result = await provisioning.Provisioner.RolloutAsync(
      "disk-2",
      null,
      4,
      TimeSpan.Zero,
      Service(),
      TestToken
    );

    await Assert.That(result.Failures["app-a"]).IsEqualTo("Deleting old-app-a failed.");
    await Assert.That(result.Retired).IsEmpty();
    await Assert.That(result.Replaced).IsEquivalentTo(["app-b"]);
    await Assert
      .That(provisioning.Output.ToString())
      .Contains("Replaced 1, retired 0, already on the image 0, failed 1.\n");
  }

  /// <summary>
  /// Tenants under <c>app-</c> in each state, an operator's tenant, all on <c>disk-1</c>, and a
  /// stopped renderer already on <c>disk-2</c>.
  /// </summary>
  private static void AddMix(Provisioning provisioning)
  {
    provisioning.AddRenderer("app-running", "disk-1");
    // Stopped a moment ago: a rollout retires stopped renderers however long they have been.
    provisioning.AddRenderer("app-stopped", "disk-1");
    provisioning.Sandboxes.Suspend("old-app-stopped", TimeSpan.FromMinutes(1));
    provisioning.AddRenderer("app-suspended", "disk-1", state: SandboxStates.Stopped);
    provisioning.AddRenderer("app-disabled", "disk-1");
    provisioning.Sandboxes.Suspend(
      "old-app-disabled",
      TimeSpan.FromDays(1),
      SandboxStoppedReasons.Disabled
    );
    provisioning.AddRenderer("app-gone", "disk-1");
    provisioning.Sandboxes.Remove("old-app-gone");
    provisioning.AddRenderer("app-current", "disk-2", state: SandboxStates.Stopped);
    provisioning.AddRenderer("contoso", "disk-1", state: SandboxStates.Stopped);
  }

  /// <summary>The service's settings: the prefix <c>app-</c>.</summary>
  private static ProvisioningServiceOptions Service()
  {
    ProvisioningServiceOptions service = new();
    service.TenantPrefixes.Add(new ManagedTenantPrefix { Prefix = "app-" });
    return service;
  }
}
