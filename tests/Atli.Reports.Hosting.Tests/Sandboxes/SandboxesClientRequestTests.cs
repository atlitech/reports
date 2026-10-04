using System.Net;
using System.Text.Json.Nodes;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Hosting.Tests.Support;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Hosting.Tests.Sandboxes;

/// <summary>
/// What each call sends to the data plane, and how the client reads the answers. The answers are the
/// shapes the data plane gave on api-version 2026-02-01-preview.
/// </summary>
public class SandboxesClientRequestTests
{
  private const string GroupUri =
    "https://management.eastus2.azuredevcompute.io/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-1/sandboxGroups/group-1/";

  private const string Id = "98c01b65-b81b-4dca-b000-fdae0eb0939c";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Create_puts_the_spec_to_the_group_with_a_bearer_token()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running")));

    var created = await sandboxes.Client.CreateAsync(
      new SandboxSpec
      {
        DiskImageId = "disk-1",
        Cpu = "1000m",
        Memory = "2048Mi",
        Entrypoint = ["/usr/bin/tini", "--", "/app/Atli.Reports.Server"],
        Environment = new Dictionary<string, string> { ["K"] = "V" },
        Labels = new Dictionary<string, string> { ["tenant"] = "acme" },
        AutoSuspendAfter = TimeSpan.FromMinutes(5),
      },
      TestToken
    );

    var request = sandboxes.Plane.Requests.Single();
    await Assert.That(request.Method).IsEqualTo(HttpMethod.Put);
    await Assert
      .That(request.Uri.ToString())
      .IsEqualTo(GroupUri + "sandboxes?api-version=2026-02-01-preview");
    await Assert.That(request.Authorization).IsEqualTo("Bearer token-1");
    await Assert.That(request.ContentType).IsEqualTo("application/json; charset=utf-8");
    await AssertJson(
      request.Body,
      """
      {
        "sourcesRef": { "diskImage": { "id": "disk-1", "isPublic": false } },
        "resources": { "cpu": "1000m", "memory": "2048Mi" },
        "egressPolicy": { "defaultAction": "Deny" },
        "entrypoint": ["/usr/bin/tini", "--", "/app/Atli.Reports.Server"],
        "environment": { "K": "V" },
        "labels": { "tenant": "acme" },
        "lifecycle": { "autoSuspendPolicy": { "enabled": true, "interval": 300, "mode": "Memory" } }
      }
      """
    );
    await Assert.That(created.Id).IsEqualTo(Id);
    await Assert.That(created.State).IsEqualTo(SandboxStates.Running);
    await Assert.That(created.Labels["tenant"]).IsEqualTo("acme");
    await Assert.That(created.Ports.Count).IsEqualTo(0);
    await Assert
      .That(sandboxes.Credential.Scopes)
      .IsEquivalentTo(["https://dynamicsessions.io/.default"]);
  }

  [Test]
  public async Task Create_without_auto_suspend_turns_it_off_and_keeps_egress_as_given()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running")));

    await sandboxes.Client.CreateAsync(
      new SandboxSpec
      {
        DiskImageId = "disk-1",
        Cpu = "500m",
        Memory = "1024Mi",
        Entrypoint = ["/bin/sleep", "infinity"],
        EgressDefaultAction = "Allow",
      },
      TestToken
    );

    await AssertJson(
      sandboxes.Plane.Requests.Single().Body,
      """
      {
        "sourcesRef": { "diskImage": { "id": "disk-1", "isPublic": false } },
        "resources": { "cpu": "500m", "memory": "1024Mi" },
        "egressPolicy": { "defaultAction": "Allow" },
        "entrypoint": ["/bin/sleep", "infinity"],
        "environment": {},
        "labels": {},
        "lifecycle": { "autoSuspendPolicy": { "enabled": false } }
      }
      """
    );
  }

  [Test]
  public async Task Create_rounds_auto_suspend_up_to_whole_seconds()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running")));

    await sandboxes.Client.CreateAsync(
      Spec() with
      {
        AutoSuspendAfter = TimeSpan.FromSeconds(90.2),
      },
      TestToken
    );

    var body = JsonNode.Parse(sandboxes.Plane.Requests.Single().Body!)!;
    await Assert
      .That(body["lifecycle"]!["autoSuspendPolicy"]!["interval"]!.GetValue<int>())
      .IsEqualTo(91);
  }

  [Test]
  [Arguments(0)]
  [Arguments(-60)]
  public async Task Create_refuses_an_auto_suspend_under_a_second(int seconds)
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running")));

    await Assert
      .That(async () =>
        await sandboxes.Client.CreateAsync(
          Spec() with
          {
            AutoSuspendAfter = TimeSpan.FromSeconds(seconds),
          },
          TestToken
        )
      )
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Get_reads_the_sandbox_and_its_ports()
  {
    var ports = $$"""
      [
        {{FakeDataPlane.Port(Id, 8080)}},
        { "port": 9090, "url": "https://{{Id}}--9090.eastus2.adcproxy.io", "auth": { "email": "a@b.c" } },
        { "port": 7070, "auth": { "anonymous": true } }
      ]
      """;
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopped", ports))
    );

    var sandbox = await sandboxes.Client.GetAsync(Id, TestToken);

    var request = sandboxes.Plane.Requests.Single();
    await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
    await Assert
      .That(request.Uri.ToString())
      .IsEqualTo($"{GroupUri}sandboxes/{Id}?api-version=2026-02-01-preview");
    await Assert.That(request.Body).IsNull();
    await Assert.That(sandbox!.Id).IsEqualTo(Id);
    await Assert.That(sandbox.State).IsEqualTo(SandboxStates.Stopped);
    // The port with no address yet is left out.
    await Assert
      .That(sandbox.Ports)
      .IsEquivalentTo([
        new SandboxPort(8080, new Uri($"https://{Id}--8080.eastus2.adcproxy.io"), true),
        new SandboxPort(9090, new Uri($"https://{Id}--9090.eastus2.adcproxy.io"), false),
      ]);
  }

  [Test]
  public async Task Get_of_a_missing_sandbox_returns_null()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(
        HttpStatusCode.NotFound,
        "SandboxNotFound",
        "Requested document not found."
      )
    );

    var sandbox = await sandboxes.Client.GetAsync(Id, TestToken);

    await Assert.That(sandbox).IsNull();
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(1);
  }

  [Test]
  public async Task List_reads_the_array_of_sandboxes()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(
        $"[{FakeDataPlane.Sandbox(Id, "Running")}, {FakeDataPlane.Sandbox("other", "Stopped")}]"
      )
    );

    var listed = await sandboxes.Client.ListAsync(TestToken);

    var request = sandboxes.Plane.Requests.Single();
    await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
    await Assert
      .That(request.Uri.ToString())
      .IsEqualTo(GroupUri + "sandboxes?api-version=2026-02-01-preview");
    await Assert
      .That(listed.Select(sandbox => sandbox.Id))
      .IsEquivalentTo([Id, "other"], CollectionOrdering.Matching);
    await Assert
      .That(listed.Select(sandbox => sandbox.State))
      .IsEquivalentTo([SandboxStates.Running, SandboxStates.Stopped], CollectionOrdering.Matching);
  }

  [Test]
  public async Task List_of_an_empty_group_is_empty()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("[]"));

    var listed = await sandboxes.Client.ListAsync(TestToken);

    await Assert.That(listed.Count).IsEqualTo(0);
  }

  [Test]
  [Arguments(HttpStatusCode.OK)]
  [Arguments(HttpStatusCode.NoContent)]
  [Arguments(HttpStatusCode.NotFound)]
  public async Task Delete_succeeds_whether_or_not_the_sandbox_exists(HttpStatusCode status)
  {
    using TestSandboxes sandboxes = new(
      status == HttpStatusCode.OK ? FakeDataPlane.Ok("{}") : FakeDataPlane.Status(status)
    );

    await sandboxes.Client.DeleteAsync(Id, TestToken);

    var request = sandboxes.Plane.Requests.Single();
    await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
    await Assert
      .That(request.Uri.ToString())
      .IsEqualTo($"{GroupUri}sandboxes/{Id}?api-version=2026-02-01-preview");
  }

  [Test]
  public async Task Stop_returns_the_sandbox_rather_than_the_snapshot_it_took()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(
        $$"""
        {
          "id": "1bd215e9-8a8c-493c-8156-105cc6b9fb8c",
          "labels": {},
          "sandboxId": "{{Id}}",
          "createdAtUtc": "2026-10-04T01:51:46.3870537Z",
          "sizeInMB": 40
        }
        """
      ),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopped"))
    );

    var stopped = await sandboxes.Client.StopAsync(Id, TestToken);

    var requests = sandboxes.Plane.Requests;
    await Assert.That(requests.Count).IsEqualTo(2);
    await Assert.That(requests[0].Method).IsEqualTo(HttpMethod.Post);
    await Assert.That(requests[0].PathInGroup).IsEqualTo($"sandboxes/{Id}/stop");
    await Assert.That(requests[0].Body).IsEqualTo("{}");
    await Assert.That(requests[1].Method).IsEqualTo(HttpMethod.Get);
    await Assert.That(stopped.Id).IsEqualTo(Id);
    await Assert.That(stopped.State).IsEqualTo(SandboxStates.Stopped);
  }

  [Test]
  public async Task Stopping_a_stopped_sandbox_succeeds()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(
        HttpStatusCode.Conflict,
        "SandboxNotRunning",
        $"Sandbox '{Id}' is not in Running state"
      ),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopped"))
    );

    var stopped = await sandboxes.Client.StopAsync(Id, TestToken);

    await Assert.That(stopped.State).IsEqualTo(SandboxStates.Stopped);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Resume_posts_and_reads_the_running_sandbox()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    var resumed = await sandboxes.Client.ResumeAsync(Id, TestToken);

    var request = sandboxes.Plane.Requests.Single();
    await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
    await Assert.That(request.PathInGroup).IsEqualTo($"sandboxes/{Id}/resume");
    await Assert.That(request.Uri.Query).IsEqualTo(FakeDataPlane.ApiVersionQuery);
    await Assert.That(request.Body).IsEqualTo("{}");
    await Assert.That(resumed.State).IsEqualTo(SandboxStates.Running);
    await Assert.That(resumed.Ports.Single().Port).IsEqualTo(8080);
  }

  [Test]
  public async Task Resuming_a_running_sandbox_succeeds()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(
        HttpStatusCode.Conflict,
        "InvalidSandboxState",
        "Sandbox must be in Stopped state to resume. Current state: Running"
      ),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running"))
    );

    var resumed = await sandboxes.Client.ResumeAsync(Id, TestToken);

    await Assert.That(resumed.State).IsEqualTo(SandboxStates.Running);
    await Assert.That(sandboxes.Plane.Requests[1].Method).IsEqualTo(HttpMethod.Get);
  }

  [Test]
  public async Task A_conflict_stands_when_the_sandbox_settles_elsewhere_without_a_transition()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(
        HttpStatusCode.Conflict,
        "PortAlreadyExists",
        "Port 8080 already exists on this sandbox"
      ),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopped", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    var exception = await Assert
      .That(async () =>
        await sandboxes.Client.AddPortAsync(
          Id,
          8080,
          anonymous: true,
          SandboxPortActivation.OnDemand,
          TestToken
        )
      )
      .Throws<SandboxesException>();

    // The port exists as Manual: no wait, no second attempt.
    await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(2);
    await Assert.That(sandboxes.Clock.HasPendingTimer).IsFalse();
  }

  [Test]
  public async Task A_resume_refused_while_the_sandbox_stops_waits_for_it_then_resumes_it()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(
        HttpStatusCode.Conflict,
        "InvalidSandboxState",
        "Sandbox must be in Stopped state to resume. Current state: Stopping"
      ),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopping")),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopped")),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running"))
    );

    var resume = sandboxes.Client.ResumeAsync(Id, TestToken);
    var poll = await sandboxes.Clock.NextTimerAsync(TestToken);
    sandboxes.Clock.Advance(poll);
    var resumed = await resume;

    await Assert.That(poll).IsEqualTo(SandboxesClient.SettlePollInterval);
    await Assert.That(resumed.State).IsEqualTo(SandboxStates.Running);
    await Assert
      .That(sandboxes.Plane.Requests.Select(request => $"{request.Method} {request.PathInGroup}"))
      .IsEquivalentTo(
        [
          $"POST sandboxes/{Id}/resume",
          $"GET sandboxes/{Id}",
          $"GET sandboxes/{Id}",
          $"POST sandboxes/{Id}/resume",
        ],
        CollectionOrdering.Matching
      );
  }

  [Test]
  public async Task A_stop_refused_while_the_sandbox_stops_succeeds_once_it_has_stopped()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.Conflict, "SandboxNotRunning", "Not running."),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopping")),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopped"))
    );

    var stop = sandboxes.Client.StopAsync(Id, TestToken);
    sandboxes.Clock.Advance(await sandboxes.Clock.NextTimerAsync(TestToken));
    var stopped = await stop;

    await Assert.That(stopped.State).IsEqualTo(SandboxStates.Stopped);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(3);
  }

  [Test]
  public async Task A_port_refused_while_its_address_is_assigned_succeeds_once_it_has_one()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.Conflict, "PortAlreadyExists", "Port 8080 exists."),
      FakeDataPlane.Ok(
        FakeDataPlane.Sandbox(
          Id,
          "Running",
          """[{ "port": 8080, "auth": { "anonymous": true } }]"""
        )
      ),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    var add = sandboxes.Client.AddPortAsync(Id, 8080, anonymous: true, TestToken);
    sandboxes.Clock.Advance(await sandboxes.Clock.NextTimerAsync(TestToken));
    var sandbox = await add;

    await Assert.That(sandbox.Ports.Single().Port).IsEqualTo(8080);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(3);
  }

  [Test]
  public async Task A_conflict_stands_when_the_sandbox_does_not_settle_within_the_wait()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(
        HttpStatusCode.Conflict,
        "InvalidSandboxState",
        "Sandbox must be in Stopped state to resume. Current state: Stopping"
      ),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopping"))
    );

    var resume = sandboxes.Client.ResumeAsync(Id, TestToken);
    var waited = TimeSpan.Zero;
    while (waited < SandboxesClient.SettleTimeout)
    {
      var poll = await sandboxes.Clock.NextTimerAsync(TestToken);
      sandboxes.Clock.Advance(poll);
      waited += poll;
    }

    var exception = await Assert.That(async () => await resume).Throws<SandboxesException>();

    await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    await Assert.That(exception.Message).Contains("Current state: Stopping");
    // The first read, and one after each second of the 30.
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(1 + 31);
    await Assert.That(sandboxes.Plane.Requests[^1].Method).IsEqualTo(HttpMethod.Get);
  }

  [Test]
  public async Task Add_port_asks_for_on_demand_activation()
  {
    var onDemand = FakeDataPlane
      .Port(Id, 8080)
      .Replace("\"Manual\"", "\"OnDemand\"", StringComparison.Ordinal);
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok($$"""{ "ports": [{{onDemand}}] }"""),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{onDemand}]"))
    );

    var sandbox = await sandboxes.Client.AddPortAsync(
      Id,
      8080,
      anonymous: true,
      SandboxPortActivation.OnDemand,
      TestToken
    );

    await AssertJson(
      sandboxes.Plane.Requests[0].Body,
      """{ "port": 8080, "auth": { "anonymous": true }, "activationMode": "OnDemand" }"""
    );
    await Assert
      .That(sandbox.Ports.Single())
      .IsEqualTo(
        new SandboxPort(8080, new Uri($"https://{Id}--8080.eastus2.adcproxy.io"), true)
        {
          Activation = SandboxPortActivation.OnDemand,
        }
      );
  }

  [Test]
  public async Task Add_port_names_manual_activation_when_asked_to()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    await sandboxes.Client.AddPortAsync(
      Id,
      8080,
      anonymous: true,
      SandboxPortActivation.Manual,
      TestToken
    );

    await AssertJson(
      sandboxes.Plane.Requests.Single().Body,
      """{ "port": 8080, "auth": { "anonymous": true }, "activationMode": "Manual" }"""
    );
  }

  [Test]
  public async Task Adding_an_on_demand_port_that_is_already_exposed_on_demand_succeeds()
  {
    var onDemand = FakeDataPlane
      .Port(Id, 8080)
      .Replace("\"Manual\"", "\"OnDemand\"", StringComparison.Ordinal);
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.Conflict, "PortAlreadyExists", "Port 8080 exists."),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopped", $"[{onDemand}]"))
    );

    var sandbox = await sandboxes.Client.AddPortAsync(
      Id,
      8080,
      anonymous: true,
      SandboxPortActivation.OnDemand,
      TestToken
    );

    await Assert.That(sandbox.Ports.Single().Activation).IsEqualTo(SandboxPortActivation.OnDemand);
  }

  [Test]
  public async Task A_client_written_before_activation_modes_exposes_only_manual_ports()
  {
    ISandboxesClient client = new ManualPortsOnly();

    var manual = await client.AddPortAsync(
      Id,
      8080,
      anonymous: true,
      SandboxPortActivation.Manual,
      TestToken
    );

    await Assert.That(manual.Id).IsEqualTo(Id);
    await Assert
      .That(async () =>
        await client.AddPortAsync(
          Id,
          8080,
          anonymous: true,
          SandboxPortActivation.OnDemand,
          TestToken
        )
      )
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task Get_reads_when_the_sandbox_was_created_and_leaves_an_unreadable_time_unknown()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running")),
      FakeDataPlane.Ok(
        FakeDataPlane
          .Sandbox(Id, "Running")
          .Replace("2026-10-04T01:50:55.1020312+00:00", "yesterday", StringComparison.Ordinal)
      )
    );

    var sandbox = await sandboxes.Client.GetAsync(Id, TestToken);
    var unknown = await sandboxes.Client.GetAsync(Id, TestToken);

    await Assert
      .That(sandbox!.CreatedAt)
      .IsEqualTo(new DateTimeOffset(2026, 10, 4, 1, 50, 55, TimeSpan.Zero).AddTicks(1020312));
    await Assert.That(unknown!.CreatedAt).IsNull();
  }

  [Test]
  public async Task Add_port_posts_the_port_and_reads_the_sandbox_afresh()
  {
    using TestSandboxes sandboxes = new(
      // The data plane answers with the ports alone.
      FakeDataPlane.Ok($$"""{ "ports": [{{FakeDataPlane.Port(Id, 8080)}}] }"""),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    var sandbox = await sandboxes.Client.AddPortAsync(Id, 8080, anonymous: true, TestToken);

    var requests = sandboxes.Plane.Requests;
    await Assert.That(requests[0].Method).IsEqualTo(HttpMethod.Post);
    await Assert.That(requests[0].PathInGroup).IsEqualTo($"sandboxes/{Id}/ports/add");
    await AssertJson(requests[0].Body, """{ "port": 8080, "auth": { "anonymous": true } }""");
    await Assert.That(requests[1].Method).IsEqualTo(HttpMethod.Get);
    await Assert.That(sandbox.Id).IsEqualTo(Id);
    await Assert
      .That(sandbox.Ports.Single())
      .IsEqualTo(new SandboxPort(8080, new Uri($"https://{Id}--8080.eastus2.adcproxy.io"), true));
  }

  [Test]
  public async Task Add_port_takes_a_whole_sandbox_when_the_answer_is_one()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    var sandbox = await sandboxes.Client.AddPortAsync(Id, 8080, anonymous: false, TestToken);

    await AssertJson(
      sandboxes.Plane.Requests.Single().Body,
      """{ "port": 8080, "auth": { "anonymous": false } }"""
    );
    await Assert.That(sandbox.Ports.Single().Port).IsEqualTo(8080);
  }

  [Test]
  public async Task Adding_a_port_that_is_already_exposed_succeeds()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(
        HttpStatusCode.Conflict,
        "PortAlreadyExists",
        "Port 8080 already exists on this sandbox"
      ),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    var sandbox = await sandboxes.Client.AddPortAsync(Id, 8080, anonymous: true, TestToken);

    await Assert.That(sandbox.Ports.Single().Port).IsEqualTo(8080);
  }

  [Test]
  public async Task Adding_a_port_exposed_with_other_access_fails()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(
        HttpStatusCode.Conflict,
        "PortAlreadyExists",
        "Port 8080 already exists on this sandbox"
      ),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    var exception = await Assert
      .That(async () => await sandboxes.Client.AddPortAsync(Id, 8080, anonymous: false, TestToken))
      .Throws<SandboxesException>();

    await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
  }

  [Test]
  [Arguments(0)]
  [Arguments(65536)]
  public async Task Add_port_refuses_a_port_out_of_range(int port)
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("{}"));

    await Assert
      .That(async () => await sandboxes.Client.AddPortAsync(Id, port, anonymous: true, TestToken))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Add_port_refuses_an_unknown_activation()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("{}"));

    await Assert
      .That(async () =>
        await sandboxes.Client.AddPortAsync(
          Id,
          8080,
          anonymous: true,
          (SandboxPortActivation)7,
          TestToken
        )
      )
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(0);
  }

  [Test]
  [Arguments("")]
  [Arguments(" ")]
  [Arguments("..")]
  [Arguments("../other-group")]
  [Arguments("a/b")]
  [Arguments("a?b")]
  [Arguments("a%2Fb")]
  public async Task Refuses_a_sandbox_ID_that_could_change_the_path(string sandboxId)
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("{}"));

    await Assert
      .That(async () => await sandboxes.Client.GetAsync(sandboxId, TestToken))
      .Throws<ArgumentException>();
    await Assert
      .That(async () => await sandboxes.Client.DeleteAsync(sandboxId, TestToken))
      .Throws<ArgumentException>();
    await Assert
      .That(async () => await sandboxes.Client.ResumeAsync(sandboxId, TestToken))
      .Throws<ArgumentException>();
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Refuses_options_without_a_group()
  {
    using HttpClient http = new();
    SandboxesOptions options = new() { SubscriptionId = "sub-1", Region = "eastus2" };

    await Assert
      .That(() => new SandboxesClient(http, new FakeCredential(TimeProvider.System), options))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Ignores_the_base_address_of_its_http_client()
  {
    FakeDataPlane plane = new(FakeDataPlane.Ok("[]"));
    using HttpClient http = new(plane) { BaseAddress = new Uri("https://elsewhere.example/x/") };
    SandboxesClient client = new(
      http,
      new FakeCredential(TimeProvider.System),
      FakeDataPlane.Options
    );

    await client.ListAsync(TestToken);

    await Assert
      .That(plane.Requests.Single().Uri.ToString())
      .IsEqualTo(GroupUri + "sandboxes?api-version=2026-02-01-preview");
  }

  private static SandboxSpec Spec() =>
    new()
    {
      DiskImageId = "disk-1",
      Cpu = "500m",
      Memory = "1024Mi",
      Entrypoint = ["/bin/sleep", "infinity"],
    };

  private static async Task AssertJson(string? actual, string expected)
  {
    await Assert.That(actual).IsNotNull();
    var equal = JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(expected));
    await Assert.That(equal).IsTrue().Because($"the body was {actual}");
  }

  /// <summary>A client as written before activation modes: it implements only the first overload.</summary>
  private sealed class ManualPortsOnly : ISandboxesClient
  {
    public Task<SandboxView> AddPortAsync(
      string sandboxId,
      int port,
      bool anonymous,
      CancellationToken cancellationToken
    ) => Task.FromResult(new SandboxView { Id = sandboxId, State = SandboxStates.Running });

    public Task<SandboxView> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken) =>
      throw new NotSupportedException();

    public Task<SandboxView?> GetAsync(string sandboxId, CancellationToken cancellationToken) =>
      throw new NotSupportedException();

    public Task<IReadOnlyList<SandboxView>> ListAsync(CancellationToken cancellationToken) =>
      throw new NotSupportedException();

    public Task DeleteAsync(string sandboxId, CancellationToken cancellationToken) =>
      throw new NotSupportedException();

    public Task<SandboxView> StopAsync(string sandboxId, CancellationToken cancellationToken) =>
      throw new NotSupportedException();

    public Task<SandboxView> ResumeAsync(string sandboxId, CancellationToken cancellationToken) =>
      throw new NotSupportedException();
  }
}
