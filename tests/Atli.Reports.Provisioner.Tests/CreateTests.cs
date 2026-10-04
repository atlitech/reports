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
        }
      );
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
          "port sandbox-1 8080 anonymous",
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
