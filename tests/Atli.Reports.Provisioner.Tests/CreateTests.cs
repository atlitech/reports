using System.Net;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner.Tests.Support;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>
/// Creating a tenant's renderer: a sandbox of its own with a credential of its own, ready before the
/// gateway can route to it, and nothing left behind when any step fails.
/// </summary>
public class CreateTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Creates_a_sandbox_from_the_disk_image_that_admits_only_the_recorded_credential()
  {
    using Provisioning provisioning = new();

    var record = await provisioning.Provisioner.CreateAsync(
      "contoso",
      RendererSize.Large,
      "disk-2",
      TestToken
    );

    await Assert.That(provisioning.Sandboxes.Created).HasSingleItem();
    var spec = provisioning.Sandboxes.Created[0];
    await Assert.That(spec.DiskImageId).IsEqualTo("disk-2");
    await Assert.That(spec.Cpu).IsEqualTo("2000m");
    await Assert.That(spec.Memory).IsEqualTo("4096Mi");
    await Assert.That(spec.Entrypoint).IsEquivalentTo(RendererServerEnvironment.Entrypoint);
    await Assert.That(spec.EgressDefaultAction).IsEqualTo("Deny");
    await Assert.That(spec.AutoSuspendAfter).IsEqualTo(TimeSpan.FromMinutes(5));
    await Assert
      .That(spec.Labels)
      .IsEquivalentTo(
        new Dictionary<string, string>
        {
          ["app"] = "atli-reports",
          ["role"] = "renderer",
          ["tenant"] = "contoso",
          ["size"] = "L",
          // Unique to this create call.
          ["launch"] = spec.Labels["launch"],
        }
      );
    await Assert.That(Guid.TryParseExact(spec.Labels["launch"], "N", out _)).IsTrue();
    // The renderer holds the verifier of exactly the credential the gateway will present.
    RendererCredential recorded = new(
      record.ApiKey.Split('.')[0],
      record.ApiKey,
      RendererCredential.VerifierOf(record.ApiKey)
    );
    await Assert
      .That(spec.Environment)
      .IsEquivalentTo(RendererServerEnvironment.Create(recorded, RendererSize.Large));
    await Assert.That(spec.Environment.Values).DoesNotContain(record.ApiKey);
  }

  [Test]
  public async Task Records_the_renderer_once_its_port_is_exposed_and_it_is_ready()
  {
    using Provisioning provisioning = new();

    var record = await provisioning.Provisioner.CreateAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );

    await Assert
      .That(provisioning.Journal.Entries)
      .IsEquivalentTo(
        [
          "create sandbox-1 (contoso)",
          // On demand by default: a request to the stopped renderer resumes it.
          "port sandbox-1 8080 anonymous OnDemand",
          "probe https://sandbox-1-8080.example.test/",
          "put contoso -> sandbox-1",
        ],
        TUnit.Assertions.Enums.CollectionOrdering.Matching
      );
    await Assert.That(provisioning.Records["contoso"]).IsEqualTo(record);
    await Assert.That(record.TenantId).IsEqualTo("contoso");
    await Assert.That(record.Url).IsEqualTo(new Uri("https://sandbox-1-8080.example.test/"));
    await Assert.That(record.SandboxId).IsEqualTo("sandbox-1");
    await Assert.That(record.DiskImageId).IsEqualTo("disk-1");
    await Assert.That(record.CreatedAt).IsEqualTo(provisioning.Clock.GetUtcNow());
    await Assert.That(record.ApiKey).StartsWith("reports-");
    // One conversion and one queued: what the renderer's environment admits.
    await Assert.That(record.MaxConcurrentRequests).IsEqualTo(2);
  }

  [Test]
  public async Task Records_what_a_large_renderer_admits()
  {
    using Provisioning provisioning = new();

    var record = await provisioning.Provisioner.CreateAsync(
      "contoso",
      RendererSize.Large,
      "disk-1",
      TestToken
    );

    await Assert.That(record.MaxConcurrentRequests).IsEqualTo(4);
    await Assert
      .That(
        provisioning.Sandboxes.Created[0].Environment[
          "ReportsServer__Limits__MaxConcurrentRequestsPerCaller"
        ]
      )
      .IsEqualTo("4");
  }

  [Test]
  public async Task Exposes_the_port_to_the_configured_source_ranges_only()
  {
    using Provisioning provisioning = new();
    provisioning.Options.AllowedSourceCidrs.AddRange(["203.0.113.7/32", "198.51.100.0/24"]);

    await provisioning.Provisioner.CreateAsync("contoso", RendererSize.Medium, "disk-1", TestToken);

    await Assert
      .That(provisioning.Journal.Matching("port"))
      .IsEquivalentTo([
        "port sandbox-1 8080 anonymous OnDemand from 203.0.113.7/32,198.51.100.0/24",
      ]);
  }

  [Test]
  public async Task Writes_the_renderer_but_never_its_credential()
  {
    using Provisioning provisioning = new();

    var record = await provisioning.Provisioner.CreateAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );

    var output = provisioning.Output.ToString();
    await Assert.That(output).Contains("Tenant:      contoso");
    await Assert.That(output).Contains("Sandbox:     sandbox-1");
    await Assert.That(output).Contains("URL:         https://sandbox-1-8080.example.test/");
    await Assert.That(output).Contains("State:       Running");
    await Assert.That(output).DoesNotContain(record.ApiKey);
    await Assert.That(output).DoesNotContain(record.ApiKey.Split('.')[1]);
    await Assert.That(output).DoesNotContain(RendererCredential.VerifierOf(record.ApiKey));
  }

  [Test]
  public async Task Renderers_never_share_a_credential()
  {
    using Provisioning provisioning = new();

    var first = await provisioning.Provisioner.CreateAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );
    var second = await provisioning.Provisioner.CreateAsync(
      "fabrikam",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );

    await Assert.That(second.ApiKey).IsNotEqualTo(first.ApiKey);
    var hash = "ReportsServer__Authentication__ApiKeys__0__Hash";
    await Assert
      .That(provisioning.Sandboxes.Created[1].Environment[hash])
      .IsNotEqualTo(provisioning.Sandboxes.Created[0].Environment[hash]);
  }

  [Test]
  public async Task Refuses_a_tenant_that_already_has_a_renderer()
  {
    using Provisioning provisioning = new();
    var existing = provisioning.AddRenderer("contoso", "disk-1");

    var exception = await Assert
      .That(async () =>
        await provisioning.Provisioner.CreateAsync(
          "contoso",
          RendererSize.Medium,
          "disk-2",
          TestToken
        )
      )
      .Throws<ProvisioningException>();

    await Assert.That(exception!.Message).Contains("already has a renderer (sandbox old-contoso)");
    await Assert.That(provisioning.Sandboxes.Created).IsEmpty();
    await Assert.That(provisioning.Records["contoso"]).IsSameReferenceAs(existing);
  }

  [Test]
  [Arguments("port")]
  [Arguments("record")]
  public async Task A_failure_after_the_sandbox_exists_deletes_it_and_writes_no_record(
    string failing
  )
  {
    using Provisioning provisioning = new();
    if (failing == "port")
    {
      provisioning.Sandboxes.FailAddPort = _ => new SandboxesException(
        "Too many ports.",
        HttpStatusCode.Conflict
      );
    }
    else
    {
      provisioning.Records.FailPut = new InvalidOperationException("The vault is unavailable.");
    }

    await Assert
      .That(async () =>
        await provisioning.Provisioner.CreateAsync(
          "contoso",
          RendererSize.Medium,
          "disk-1",
          TestToken
        )
      )
      .Throws<Exception>();

    await Assert.That(provisioning.Journal.Matching("delete")).IsEquivalentTo(["delete sandbox-1"]);
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
    await Assert.That(provisioning.Records.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Exposes_a_manual_port_when_configured_to()
  {
    using Provisioning provisioning = new();
    provisioning.Options.PortActivation = "manual";

    await provisioning.Provisioner.CreateAsync("contoso", RendererSize.Medium, "disk-1", TestToken);

    await Assert
      .That(provisioning.Journal.Matching("port"))
      .IsEquivalentTo(["port sandbox-1 8080 anonymous Manual"]);
  }

  [Test]
  public async Task A_record_write_that_failed_after_writing_keeps_the_renderer()
  {
    using Provisioning provisioning = new();
    provisioning.Records.FailPutAfterWriting = new TimeoutException("The answer was lost.");

    var record = await provisioning.Provisioner.CreateAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );

    // Deleting the sandbox would leave the record pointing at nothing.
    await Assert.That(provisioning.Records["contoso"]).IsEqualTo(record);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["sandbox-1"]);
    await Assert.That(provisioning.Journal.Matching("delete")).IsEmpty();
    await Assert
      .That(provisioning.Output.ToString())
      .Contains(
        "[contoso] Writing the record reported a failure (The answer was lost.), but the record "
          + "points to sandbox sandbox-1."
      );
  }

  [Test]
  public async Task A_record_write_whose_outcome_cannot_be_read_back_keeps_the_sandbox_for_prune()
  {
    using Provisioning provisioning = new();
    provisioning.Records.BeforePut = () =>
      provisioning.Records.FailGet = new InvalidOperationException("The vault is unavailable.");
    provisioning.Records.FailPut = new TimeoutException("No answer.");

    await Assert
      .That(async () =>
        await provisioning.Provisioner.CreateAsync(
          "contoso",
          RendererSize.Medium,
          "disk-1",
          TestToken
        )
      )
      .Throws<TimeoutException>();

    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["sandbox-1"]);
    await Assert
      .That(provisioning.Output.ToString())
      .Contains("[contoso] Could not tell whether the record points to sandbox sandbox-1");
  }

  [Test]
  public async Task A_create_that_another_create_beat_is_discarded()
  {
    using Provisioning provisioning = new();
    // Another create finishes while this one waits for its renderer.
    var other = provisioning.AddRenderer("contoso", "disk-1");
    await provisioning.Records.DeleteAsync("contoso", TestToken);
    provisioning.Readiness.Answer = (_, _) =>
    {
      provisioning.Records.Add(other);
      return Task.FromResult(ReadinessAnswer.Ready);
    };

    var exception = await Assert
      .That(async () =>
        await provisioning.Provisioner.CreateAsync(
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
        "Tenant contoso got a renderer (sandbox old-contoso) from another command meanwhile; "
          + "this one is discarded."
      );
    await Assert.That(provisioning.Records["contoso"]).IsSameReferenceAs(other);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-contoso"]);
    await Assert.That(provisioning.Journal.Matching("put")).IsEmpty();
  }

  [Test]
  public async Task A_create_whose_answer_was_lost_deletes_the_sandbox_it_made_by_its_launch_label()
  {
    using Provisioning provisioning = new();
    // Another renderer of the tenant's, from another launch, stays.
    provisioning.Sandboxes.Add("other", RendererLabels.For("contoso", RendererSize.Medium, "x"));
    provisioning.Sandboxes.FailCreateAfterCreating = _ => new SandboxesException(
      "Sandboxes PUT sandboxes failed: The response ended prematurely."
    );

    await Assert
      .That(async () =>
        await provisioning.Provisioner.CreateAsync(
          "contoso",
          RendererSize.Medium,
          "disk-1",
          TestToken
        )
      )
      .Throws<SandboxesException>();

    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["other"]);
    await Assert.That(provisioning.Journal.Matching("delete")).IsEquivalentTo(["delete sandbox-1"]);
    await Assert.That(provisioning.Records.Count).IsEqualTo(0);
  }

  [Test]
  public async Task A_tenant_with_an_unreadable_record_is_refused()
  {
    using Provisioning provisioning = new();
    provisioning.Records.AddUnreadable("contoso");

    var exception = await Assert
      .That(async () =>
        await provisioning.Provisioner.CreateAsync(
          "contoso",
          RendererSize.Medium,
          "disk-1",
          TestToken
        )
      )
      .Throws<ProvisioningException>();

    await Assert
      .That(exception!.Message)
      .StartsWith("Tenant contoso has a record that cannot be read");
    await Assert.That(provisioning.Sandboxes.Created).IsEmpty();
  }

  [Test]
  public async Task Waits_until_the_renderer_answers_ready()
  {
    using Provisioning provisioning = new();
    // The platform's proxy before the server listens, then the server before its browser is up.
    provisioning.Readiness.AnswerInTurn(
      ReadinessAnswer.NotReady("HTTP 403"),
      ReadinessAnswer.NotReady("HTTP 503"),
      ReadinessAnswer.Ready
    );
    var poll = RendererProvisioner.ReadyPollInterval;

    var create = provisioning.Provisioner.CreateAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );
    await provisioning.Clock.WaitForTimerAsync(poll);
    provisioning.Clock.Advance(poll);
    await provisioning.Clock.WaitForTimerAsync(poll);
    await Assert.That(provisioning.Records.Count).IsEqualTo(0);
    provisioning.Clock.Advance(poll);
    await create;

    await Assert.That(provisioning.Journal.Matching("probe")).Count().IsEqualTo(3);
    await Assert.That(provisioning.Records["contoso"]).IsNotNull();
  }

  [Test]
  public async Task A_renderer_not_ready_within_the_ready_timeout_is_deleted()
  {
    using Provisioning provisioning = new();
    provisioning.Options.ReadyTimeout = TimeSpan.FromSeconds(2);
    provisioning.Readiness.AnswerInTurn(ReadinessAnswer.NotReady("HTTP 503"));
    var poll = RendererProvisioner.ReadyPollInterval;

    var create = provisioning.Provisioner.CreateAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      TestToken
    );
    // Probes at 0, 0.5, 1, 1.5, and 2 seconds; the last one is past the timeout.
    for (var wait = 0; wait < 4; wait++)
    {
      await provisioning.Clock.WaitForTimerAsync(poll);
      await Assert.That(create.IsCompleted).IsFalse();
      provisioning.Clock.Advance(poll);
    }

    var exception = await Assert.That(async () => await create).Throws<ProvisioningException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo("The renderer was not ready within 00:00:02; it last answered HTTP 503.");
    await Assert.That(provisioning.Journal.Matching("probe")).Count().IsEqualTo(5);
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
    await Assert.That(provisioning.Records.Count).IsEqualTo(0);
  }

  [Test]
  public async Task A_canceled_create_still_deletes_its_sandbox()
  {
    using Provisioning provisioning = new();
    provisioning.Readiness.AnswerInTurn(ReadinessAnswer.NotReady("HTTP 503"));
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);

    var create = provisioning.Provisioner.CreateAsync(
      "contoso",
      RendererSize.Medium,
      "disk-1",
      cancellation.Token
    );
    await provisioning.Clock.WaitForTimerAsync(RendererProvisioner.ReadyPollInterval);
    await cancellation.CancelAsync();

    await Assert.That(async () => await create).Throws<OperationCanceledException>();
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
    await Assert.That(provisioning.Records.Count).IsEqualTo(0);
  }
}
