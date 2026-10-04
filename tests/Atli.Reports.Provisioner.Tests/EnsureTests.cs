using System.Net;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner.Tests.Support;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>
/// Ensuring a tenant's renderer, for the provisioning service: an existing renderer is found as it
/// is, a missing one is created, a record whose sandbox is gone is repaired, and a create that
/// another replica finished first counts as found.
/// </summary>
public class EnsureTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Finds_a_renderer_whose_sandbox_exists()
  {
    using Provisioning provisioning = new();
    var existing = provisioning.AddRenderer("contoso", "disk-1");

    var result = await provisioning.Provisioner.EnsureAsync(
      "contoso",
      RendererSize.Medium,
      "disk-2",
      TestToken
    );

    await Assert.That(result.Created).IsFalse();
    await Assert.That(result.Record).IsSameReferenceAs(existing);
    // Not even another disk image makes it replace a renderer: that is a rollout's job.
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
    await Assert.That(provisioning.Output.ToString()).IsEmpty();
  }

  [Test]
  public async Task Finds_a_disabled_renderer_and_leaves_it_disabled()
  {
    using Provisioning provisioning = new();
    var existing = provisioning.AddRenderer("contoso", "disk-1");
    await provisioning.Sandboxes.DisableAsync("old-contoso", TestToken);

    var result = await provisioning.Provisioner.EnsureAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );

    // The kill switch wins: the tenant's conversions keep failing until an operator acts.
    await Assert.That(result.Created).IsFalse();
    await Assert.That(result.Record).IsSameReferenceAs(existing);
    await Assert.That(provisioning.Sandboxes.Disabled).IsEquivalentTo(["old-contoso"]);
    await Assert.That(provisioning.Journal.Entries).IsEquivalentTo(["disable old-contoso"]);
  }

  [Test]
  public async Task Finds_a_record_that_names_no_sandbox()
  {
    using Provisioning provisioning = new();
    RendererRecord existing = new()
    {
      TenantId = "contoso",
      Url = new Uri("https://renderer.example.test/"),
      ApiKey = RendererCredential.Generate().Credential,
    };
    provisioning.Records.Add(existing);

    var result = await provisioning.Provisioner.EnsureAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );

    await Assert.That(result.Created).IsFalse();
    await Assert.That(result.Record).IsSameReferenceAs(existing);
    await Assert.That(provisioning.Sandboxes.Created).IsEmpty();
  }

  [Test]
  public async Task Creates_a_renderer_for_a_tenant_without_one()
  {
    using Provisioning provisioning = new();

    var result = await provisioning.Provisioner.EnsureAsync(
      "contoso",
      RendererSize.Large,
      "disk-2",
      TestToken
    );

    await Assert.That(result.Created).IsTrue();
    await Assert.That(provisioning.Records["contoso"]).IsEqualTo(result.Record);
    await Assert.That(result.Record.SandboxId).IsEqualTo("sandbox-1");
    await Assert.That(result.Record.DiskImageId).IsEqualTo("disk-2");
    await Assert.That(provisioning.Sandboxes.Created[0].Labels["size"]).IsEqualTo("L");
    await Assert
      .That(provisioning.Journal.Entries)
      .IsEquivalentTo(
        [
          "create sandbox-1 (contoso)",
          "port sandbox-1 8080 anonymous OnDemand",
          "probe https://sandbox-1-8080.example.test/",
          "put contoso -> sandbox-1",
        ],
        CollectionOrdering.Matching
      );
  }

  [Test]
  public async Task Replaces_a_sandbox_that_is_gone_and_nothing_else()
  {
    using Provisioning provisioning = new();
    var gone = provisioning.AddRenderer("contoso", "disk-1");
    var other = provisioning.AddRenderer("fabrikam", "disk-1");
    provisioning.Sandboxes.Remove("old-contoso");

    var result = await provisioning.Provisioner.EnsureAsync(
      "contoso",
      RendererSize.Small,
      "disk-2",
      TestToken
    );

    await Assert.That(result.Created).IsTrue();
    await Assert.That(provisioning.Records["contoso"]!.SandboxId).IsEqualTo("sandbox-1");
    await Assert.That(result.Record.ApiKey).IsNotEqualTo(gone.ApiKey);
    await Assert.That(provisioning.Records["fabrikam"]).IsSameReferenceAs(other);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-fabrikam", "sandbox-1"]);
    await Assert.That(provisioning.Journal.Matching("delete")).IsEmpty();
    await Assert
      .That(provisioning.Output.ToString())
      .StartsWith("[contoso] Sandbox old-contoso of the record no longer exists; replacing it.\n");
  }

  [Test]
  public async Task A_record_that_cannot_be_read_is_an_error()
  {
    using Provisioning provisioning = new();
    provisioning.Records.AddUnreadable("contoso");

    var exception = await Assert
      .That(async () =>
        await provisioning.Provisioner.EnsureAsync(
          "contoso",
          RendererSize.Medium,
          "disk-1",
          TestToken
        )
      )
      .Throws<ProvisioningException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo(
        "Tenant contoso has a record that cannot be read (The record of contoso is damaged.); "
          + "fix it, or delete the tenant."
      );
    await Assert.That(provisioning.Sandboxes.Created).IsEmpty();
    await Assert.That(provisioning.Records.HasUnreadable("contoso")).IsTrue();
  }

  [Test]
  public async Task A_create_another_replica_finished_first_is_found_and_this_one_discarded()
  {
    using Provisioning provisioning = new();
    // The other replica's renderer is recorded while this one waits for its own to be ready.
    var other = provisioning.AddRenderer("contoso", "disk-1");
    await provisioning.Records.DeleteAsync("contoso", TestToken);
    provisioning.Readiness.Answer = (_, _) =>
    {
      provisioning.Records.Add(other);
      return Task.FromResult(ReadinessAnswer.Ready);
    };

    var result = await provisioning.Provisioner.EnsureAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );

    await Assert.That(result.Created).IsFalse();
    await Assert.That(result.Record).IsSameReferenceAs(other);
    await Assert.That(provisioning.Records["contoso"]).IsSameReferenceAs(other);
    // This replica's sandbox is deleted, and the record was never written over.
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-contoso"]);
    await Assert.That(provisioning.Journal.Matching("put")).IsEmpty();
    await Assert
      .That(provisioning.Output.ToString())
      .Contains(
        "[contoso] Another command or replica gave the tenant sandbox old-contoso meanwhile; "
          + "this launch was discarded."
      );
  }

  [Test]
  public async Task A_replacement_another_replica_finished_first_is_found()
  {
    using Provisioning provisioning = new();
    var gone = provisioning.AddRenderer("contoso", "disk-1");
    provisioning.Sandboxes.Remove("old-contoso");
    provisioning.Sandboxes.Add("other", RendererLabels.For("contoso", RendererSize.Medium));
    var other = gone with
    {
      SandboxId = "other",
      Url = new Uri("https://other-8080.example.test/"),
    };
    provisioning.Readiness.Answer = (_, _) =>
    {
      provisioning.Records.Add(other);
      return Task.FromResult(ReadinessAnswer.Ready);
    };

    var result = await provisioning.Provisioner.EnsureAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );

    await Assert.That(result.Created).IsFalse();
    await Assert.That(result.Record).IsSameReferenceAs(other);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["other"]);
  }

  [Test]
  public async Task A_failed_create_is_an_error_and_leaves_nothing_behind()
  {
    using Provisioning provisioning = new();
    provisioning.Sandboxes.FailAddPort = _ => new SandboxesException(
      "Too many ports.",
      HttpStatusCode.Conflict
    );

    await Assert
      .That(async () =>
        await provisioning.Provisioner.EnsureAsync(
          "contoso",
          RendererSize.Medium,
          "disk-1",
          TestToken
        )
      )
      .Throws<SandboxesException>();

    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
    await Assert.That(provisioning.Records.Count).IsEqualTo(0);
  }

  [Test]
  public async Task A_canceled_create_deletes_its_sandbox()
  {
    using Provisioning provisioning = new();
    provisioning.Readiness.AnswerInTurn(ReadinessAnswer.NotReady("HTTP 503"));
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);

    var ensure = provisioning.Provisioner.EnsureAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      cancellation.Token
    );
    await provisioning.Clock.WaitForTimerAsync(RendererProvisioner.ReadyPollInterval);
    await cancellation.CancelAsync();

    await Assert.That(async () => await ensure).Throws<OperationCanceledException>();
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
    await Assert.That(provisioning.Records.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Writes_its_progress_but_never_the_credential()
  {
    using Provisioning provisioning = new();

    var result = await provisioning.Provisioner.EnsureAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );

    var output = provisioning.Output.ToString();
    await Assert.That(output).Contains("[contoso] Creating a sandbox from disk image disk-1");
    await Assert.That(output).EndsWith("[contoso] The record now points to sandbox sandbox-1.\n");
    await Assert.That(output).DoesNotContain(result.Record.ApiKey);
    await Assert.That(output).DoesNotContain(result.Record.ApiKey.Split('.')[1]);
    await Assert.That(output).DoesNotContain(RendererCredential.VerifierOf(result.Record.ApiKey));
  }

  [Test]
  public async Task Find_returns_only_a_renderer_whose_sandbox_exists()
  {
    using Provisioning provisioning = new();
    var existing = provisioning.AddRenderer("a", "disk-1");
    provisioning.AddRenderer("b", "disk-1");
    provisioning.Sandboxes.Remove("old-b");

    await Assert
      .That(await provisioning.Provisioner.FindAsync("a", TestToken))
      .IsSameReferenceAs(existing);
    await Assert.That(await provisioning.Provisioner.FindAsync("b", TestToken)).IsNull();
    await Assert.That(await provisioning.Provisioner.FindAsync("c", TestToken)).IsNull();
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
  }
}
