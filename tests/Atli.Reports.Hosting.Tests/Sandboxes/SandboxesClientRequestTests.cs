using System.Net;
using System.Text.Json.Nodes;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Hosting.Tests.Support;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Hosting.Tests.Sandboxes;

/// <summary>
/// What each call sends to the data plane, and how the client reads the answers. The answers are the
/// shapes the data plane gave on api-version 2026-02-01-preview, and for listings on
/// 2026-09-01-preview.
/// </summary>
public class SandboxesClientRequestTests
{
  private const string GroupUri =
    "https://management.eastus2.azuredevcompute.io/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-1/sandboxGroups/group-1/";

  private const string Id = "98c01b65-b81b-4dca-b000-fdae0eb0939c";

  /// <summary>The first page of a listing: the paged API version, a page of 100.</summary>
  private const string FirstPage =
    GroupUri + "sandboxes?api-version=2026-09-01-preview&pageSize=100";

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
  public async Task Create_starts_the_sandbox_in_the_named_network_connection()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running")));

    await sandboxes.Client.CreateAsync(
      Spec() with
      {
        NetworkConnectionName = "renderers",
      },
      TestToken
    );

    var body = JsonNode.Parse(sandboxes.Plane.Requests.Single().Body!)!;
    await Assert
      .That(body["customerVnetConnectionName"]!.GetValue<string>())
      .IsEqualTo("renderers");
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
  [Arguments("Stopped", "Disabled", true)]
  [Arguments("Stopped", "UserStopped", false)]
  [Arguments("Stopped", "Idle", false)]
  // The data plane keeps the last reason after a resume.
  [Arguments("Running", "Disabled", false)]
  public async Task Get_reads_why_a_sandbox_stopped(string state, string reason, bool disabled)
  {
    // As the data plane answered for a disabled sandbox on 2026-10-04.
    var json = $$"""
      {
        "id": "{{Id}}",
        "state": "{{state}}",
        "stateDetails": { "stoppedReason": "{{reason}}", "stoppedAt": "2026-10-04T03:58:04.6124235+00:00" },
        "ports": []
      }
      """;
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok(json));

    var sandbox = await sandboxes.Client.GetAsync(Id, TestToken);

    await Assert.That(sandbox!.StoppedReason).IsEqualTo(reason);
    await Assert.That(sandbox.IsDisabled).IsEqualTo(disabled);
  }

  [Test]
  public async Task Get_without_state_details_has_no_stopped_reason()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running")));

    var sandbox = await sandboxes.Client.GetAsync(Id, TestToken);

    await Assert.That(sandbox!.StoppedReason).IsNull();
    await Assert.That(sandbox.IsDisabled).IsFalse();
    await Assert.That(sandbox.StoppedAt).IsNull();
  }

  [Test]
  public async Task Get_reads_when_a_sandbox_stopped_and_leaves_an_unreadable_time_unknown()
  {
    static string Stopped(string stateDetails) =>
      $$"""
        { "id": "{{Id}}", "state": "Stopped", "stateDetails": {{stateDetails}}, "ports": [] }
        """;
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(
        Stopped("""{ "stoppedReason": "Idle", "stoppedAt": "2026-10-04T03:58:04.6124235+00:00" }""")
      ),
      FakeDataPlane.Ok(Stopped("""{ "stoppedReason": "Idle", "stoppedAt": "a while ago" }""")),
      FakeDataPlane.Ok(Stopped("""{ "stoppedReason": "Idle" }"""))
    );

    var sandbox = await sandboxes.Client.GetAsync(Id, TestToken);
    var unreadable = await sandboxes.Client.GetAsync(Id, TestToken);
    var absent = await sandboxes.Client.GetAsync(Id, TestToken);

    await Assert
      .That(sandbox!.StoppedAt)
      .IsEqualTo(new DateTimeOffset(2026, 10, 4, 3, 58, 4, TimeSpan.Zero).AddTicks(6124235));
    // The rest of the sandbox is read all the same.
    await Assert.That(unreadable!.StoppedAt).IsNull();
    await Assert.That(unreadable.State).IsEqualTo(SandboxStates.Stopped);
    await Assert.That(unreadable.StoppedReason).IsEqualTo(SandboxStoppedReasons.Idle);
    await Assert.That(absent!.StoppedAt).IsNull();
    await Assert.That(absent.StoppedReason).IsEqualTo(SandboxStoppedReasons.Idle);
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
  public async Task List_reads_a_page_of_sandboxes_on_the_paged_api_version()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(
        $$"""{"value": [{{FakeDataPlane.Sandbox(Id, "Running")}}, {{FakeDataPlane.Sandbox(
          "other",
          "Stopped"
        )}}]}"""
      )
    );

    var listed = await sandboxes.Client.ListAsync(TestToken);

    var request = sandboxes.Plane.Requests.Single();
    await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
    await Assert.That(request.Uri.ToString()).IsEqualTo(FirstPage);
    await Assert.That(request.Authorization).IsEqualTo("Bearer token-1");
    await Assert
      .That(listed.Select(sandbox => sandbox.Id))
      .IsEquivalentTo([Id, "other"], CollectionOrdering.Matching);
    await Assert
      .That(listed.Select(sandbox => sandbox.State))
      .IsEquivalentTo([SandboxStates.Running, SandboxStates.Stopped], CollectionOrdering.Matching);
  }

  [Test]
  public async Task List_follows_every_next_link_to_the_last_page()
  {
    var second = FirstPage + "&skipToken=page-2";
    var third = FirstPage + "&skipToken=page-3";
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(Page(second, Id, "a")),
      FakeDataPlane.Ok(Page(third, "b")),
      FakeDataPlane.Ok(Page(null, "c"))
    );

    var listed = await sandboxes.Client.ListAsync(TestToken);

    await Assert
      .That(sandboxes.Plane.Requests.Select(request => request.Uri.ToString()))
      .IsEquivalentTo([FirstPage, second, third], CollectionOrdering.Matching);
    await Assert
      .That(sandboxes.Plane.Requests.All(request => request.Authorization == "Bearer token-1"))
      .IsTrue();
    await Assert
      .That(listed.Select(sandbox => sandbox.Id))
      .IsEquivalentTo([Id, "a", "b", "c"], CollectionOrdering.Matching);
  }

  [Test]
  public async Task List_lists_a_sandbox_on_two_pages_once_as_the_later_page_has_it()
  {
    var second = FirstPage + "&skipToken=page-2";
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(
        $$"""{"value": [{{FakeDataPlane.Sandbox(Id, "Running")}}], "nextLink": "{{second}}"}"""
      ),
      FakeDataPlane.Ok(
        $$"""{"value": [{{FakeDataPlane.Sandbox("other", "Running")}}, {{FakeDataPlane.Sandbox(
          Id,
          "Stopped"
        )}}]}"""
      )
    );

    var listed = await sandboxes.Client.ListAsync(TestToken);

    await Assert
      .That(listed.Select(sandbox => sandbox.Id))
      .IsEquivalentTo([Id, "other"], CollectionOrdering.Matching);
    await Assert.That(listed[0].State).IsEqualTo(SandboxStates.Stopped);
  }

  [Test]
  [Arguments(
    "https://elsewhere.example/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-1/sandboxGroups/group-1/sandboxes?skipToken=2"
  )]
  [Arguments(
    "http://management.eastus2.azuredevcompute.io/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-1/sandboxGroups/group-1/sandboxes?skipToken=2"
  )]
  [Arguments(
    "https://management.eastus2.azuredevcompute.io:8443/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-1/sandboxGroups/group-1/sandboxes?skipToken=2"
  )]
  [Arguments(
    "https://management.eastus2.azuredevcompute.io/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-1/sandboxGroups/group-2/sandboxes?skipToken=2"
  )]
  [Arguments(
    "/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-1/sandboxGroups/group-1/sandboxes?skipToken=2"
  )]
  public async Task List_fails_on_a_next_link_to_another_address_without_sending_the_token_there(
    string nextLink
  )
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok(Page(nextLink, Id)));

    var exception = await Assert
      .That(async () => await sandboxes.Client.ListAsync(TestToken))
      .Throws<SandboxesException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo(
        "Sandboxes GET sandboxes answered with a next page at another address; the listing is not read whole."
      );
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(1);
  }

  [Test]
  public async Task List_fails_on_a_next_link_it_has_followed_before()
  {
    var second = FirstPage + "&skipToken=page-2";
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(Page(second, Id)),
      FakeDataPlane.Ok(Page(second, "other"))
    );

    var exception = await Assert
      .That(async () => await sandboxes.Client.ListAsync(TestToken))
      .Throws<SandboxesException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo(
        "Sandboxes GET sandboxes answered with a next page it had already given; the listing is not read whole."
      );
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(2);
  }

  [Test]
  public async Task List_stops_after_the_most_pages()
  {
    var pages = 0;
    using TestSandboxes sandboxes = new(_ =>
    {
      pages++;
      return FakeDataPlane.Ok(Page(FirstPage + "&skipToken=" + pages, "s" + pages))(null!);
    });

    var exception = await Assert
      .That(async () => await sandboxes.Client.ListAsync(TestToken))
      .Throws<SandboxesException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo(
        $"Sandboxes GET sandboxes answered with more than {SandboxesClient.MaxListPages} pages; the listing is not read whole."
      );
    await Assert.That(pages).IsEqualTo(SandboxesClient.MaxListPages);
  }

  [Test]
  public async Task List_takes_a_bare_array_shorter_than_a_page_whole()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok($"[{FakeDataPlane.Sandbox(Id, "Running")}]")
    );

    var listed = await sandboxes.Client.ListAsync(TestToken);

    await Assert.That(listed.Select(sandbox => sandbox.Id)).IsEquivalentTo([Id]);
  }

  [Test]
  public async Task List_fails_on_a_bare_array_as_long_as_a_page()
  {
    var full = string.Join(
      ", ",
      Enumerable
        .Range(0, SandboxesClient.ListPageSize)
        .Select(index => FakeDataPlane.Sandbox("s" + index, "Running"))
    );
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok($"[{full}]"));

    var exception = await Assert
      .That(async () => await sandboxes.Client.ListAsync(TestToken))
      .Throws<SandboxesException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo(
        $"Sandboxes GET sandboxes answered with a full page of {SandboxesClient.ListPageSize} sandboxes and no link to the next; the listing is not read whole."
      );
  }

  [Test]
  public async Task List_fails_on_a_page_without_its_sandboxes()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("""{"nextLink": null}"""));

    await Assert
      .That(async () => await sandboxes.Client.ListAsync(TestToken))
      .Throws<SandboxesException>();
  }

  [Test]
  public async Task List_of_an_empty_group_is_empty()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("""{"value": []}"""));

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
      .That(async () => await sandboxes.Client.AddPortAsync(Id, 8080, OnDemand(), TestToken))
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

    var sandbox = await sandboxes.Client.AddPortAsync(Id, 8080, OnDemand(), TestToken);

    // No source ranges: no IP access control at all.
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
      new SandboxPortOptions { Anonymous = true },
      TestToken
    );

    await AssertJson(
      sandboxes.Plane.Requests.Single().Body,
      """{ "port": 8080, "auth": { "anonymous": true }, "activationMode": "Manual" }"""
    );
  }

  [Test]
  public async Task Add_port_admits_only_the_given_source_ranges_ten_to_a_rule()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );
    string[] cidrs =
    [
      .. Enumerable.Range(1, 11).Select(host => $"203.0.113.{host}/32"),
      "2001:db8::/48",
    ];

    await sandboxes.Client.AddPortAsync(
      Id,
      8080,
      OnDemand() with
      {
        AllowedSourceCidrs = cidrs,
      },
      TestToken
    );

    await AssertJson(
      sandboxes.Plane.Requests.Single().Body,
      """
      {
        "port": 8080,
        "auth": { "anonymous": true },
        "activationMode": "OnDemand",
        "ipAccessControl": {
          "defaultAction": "Deny",
          "rules": [
            {
              "name": "gateway-1",
              "action": "Allow",
              "priority": 10,
              "sourceCidrs": [
                "203.0.113.1/32", "203.0.113.2/32", "203.0.113.3/32", "203.0.113.4/32",
                "203.0.113.5/32", "203.0.113.6/32", "203.0.113.7/32", "203.0.113.8/32",
                "203.0.113.9/32", "203.0.113.10/32"
              ]
            },
            {
              "name": "gateway-2",
              "action": "Allow",
              "priority": 20,
              "sourceCidrs": ["203.0.113.11/32", "2001:db8::/48"]
            }
          ]
        }
      }
      """
    );
  }

  [Test]
  public async Task One_rule_of_source_ranges_is_named_for_the_gateway()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    await sandboxes.Client.AddPortAsync(
      Id,
      8080,
      OnDemand() with
      {
        AllowedSourceCidrs = ["198.51.100.7/32", "198.51.100.8/32"],
      },
      TestToken
    );

    var body = JsonNode.Parse(sandboxes.Plane.Requests.Single().Body!)!;
    await AssertJson(
      body["ipAccessControl"]!.ToJsonString(),
      """
      {
        "defaultAction": "Deny",
        "rules": [
          {
            "name": "gateway",
            "action": "Allow",
            "priority": 10,
            "sourceCidrs": ["198.51.100.7/32", "198.51.100.8/32"]
          }
        ]
      }
      """
    );
  }

  [Test]
  [Arguments("203.0.113.7")]
  [Arguments("203.0.113.7/33")]
  [Arguments("203.0.113.7/24")]
  [Arguments("203.0.113.7/+32")]
  [Arguments(" 203.0.113.7/32")]
  [Arguments("203.0.113.7/32 ")]
  [Arguments("10/32")]
  [Arguments("2001:db8::1/48")]
  [Arguments("fe80::1%1/128")]
  [Arguments("example.com/32")]
  [Arguments("/32")]
  [Arguments("")]
  public async Task Add_port_refuses_a_source_range_that_is_not_a_cidr(string cidr)
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("{}"));

    var exception = await Assert
      .That(async () =>
        await sandboxes.Client.AddPortAsync(
          Id,
          8080,
          OnDemand() with
          {
            AllowedSourceCidrs = [cidr],
          },
          TestToken
        )
      )
      .Throws<ArgumentException>();

    await Assert.That(exception!.Message).Contains("is not a source range in CIDR notation");
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Add_port_refuses_more_source_ranges_than_ten_rules_hold()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("{}"));
    string[] cidrs = [.. Enumerable.Range(0, 101).Select(host => $"10.0.0.{host}/32")];

    await Assert
      .That(async () =>
        await sandboxes.Client.AddPortAsync(
          Id,
          8080,
          OnDemand() with
          {
            AllowedSourceCidrs = cidrs,
          },
          TestToken
        )
      )
      .Throws<ArgumentException>();
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Get_reads_a_ports_allowed_source_ranges()
  {
    var restricted = FakeDataPlane
      .Port(Id, 8080)
      .Replace(
        "\"protocol\"",
        """
        "ipAccessControl": {
          "defaultAction": "Deny",
          "rules": [
            { "name": "gateway-1", "action": "Allow", "priority": 10, "sourceCidrs": ["203.0.113.7/32"] },
            { "name": "gateway-2", "action": "Allow", "priority": 20, "sourceCidrs": ["2001:db8::/48"] },
            { "name": "other", "action": "Deny", "priority": 30, "sourceCidrs": ["192.0.2.0/24"] }
          ]
        },
        "protocol"
        """,
        StringComparison.Ordinal
      );
    var odd = FakeDataPlane
      .Port(Id, 9090)
      .Replace(
        "\"protocol\"",
        "\"ipAccessControl\": \"Deny\", \"protocol\"",
        StringComparison.Ordinal
      );
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{restricted}, {odd}]"))
    );

    var sandbox = await sandboxes.Client.GetAsync(Id, TestToken);

    await Assert
      .That(sandbox!.Ports[0].AllowedSourceCidrs)
      .IsEquivalentTo(["203.0.113.7/32", "2001:db8::/48"], CollectionOrdering.Matching);
    // A shape this client does not expect leaves the ranges unknown, not the sandbox unreadable.
    await Assert.That(sandbox.Ports[1].AllowedSourceCidrs).IsEmpty();
  }

  [Test]
  public async Task Adding_a_port_already_exposed_with_other_source_ranges_fails()
  {
    var restricted = FakeDataPlane
      .Port(Id, 8080)
      .Replace("\"Manual\"", "\"OnDemand\"", StringComparison.Ordinal)
      .Replace(
        "\"protocol\"",
        """
        "ipAccessControl": { "defaultAction": "Deny", "rules": [
          { "name": "gateway", "action": "Allow", "priority": 10, "sourceCidrs": ["192.0.2.1/32"] }
        ] },
        "protocol"
        """,
        StringComparison.Ordinal
      );
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.Conflict, "PortAlreadyExists", "Port 8080 exists."),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{restricted}]"))
    );

    await Assert
      .That(async () =>
        await sandboxes.Client.AddPortAsync(
          Id,
          8080,
          OnDemand() with
          {
            AllowedSourceCidrs = ["203.0.113.7/32"],
          },
          TestToken
        )
      )
      .Throws<SandboxesException>();
  }

  [Test]
  [Arguments("disable")]
  [Arguments("enable")]
  public async Task Disable_and_enable_post_and_read_the_sandbox_afresh(string action)
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Status(HttpStatusCode.NoContent),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Stopped"))
    );

    var sandbox =
      action == "disable"
        ? await sandboxes.Client.DisableAsync(Id, TestToken)
        : await sandboxes.Client.EnableAsync(Id, TestToken);

    var requests = sandboxes.Plane.Requests;
    await Assert.That(requests[0].Method).IsEqualTo(HttpMethod.Post);
    await Assert.That(requests[0].PathInGroup).IsEqualTo($"sandboxes/{Id}/{action}");
    await Assert.That(requests[0].Body).IsEqualTo("{}");
    await Assert.That(requests[1].Method).IsEqualTo(HttpMethod.Get);
    await Assert.That(sandbox.State).IsEqualTo(SandboxStates.Stopped);
  }

  [Test]
  public async Task A_refused_disable_fails_with_the_data_planes_reason()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.Forbidden, "AuthorizationFailed", "No access.")
    );

    var exception = await Assert
      .That(async () => await sandboxes.Client.DisableAsync(Id, TestToken))
      .Throws<SandboxesException>();

    await Assert
      .That(exception!.Message)
      .IsEqualTo(
        $"Sandboxes POST sandboxes/{Id}/disable failed with 403 (Forbidden): AuthorizationFailed: No access."
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

    var sandbox = await sandboxes.Client.AddPortAsync(Id, 8080, OnDemand(), TestToken);

    await Assert.That(sandbox.Ports.Single().Activation).IsEqualTo(SandboxPortActivation.OnDemand);
  }

  [Test]
  public async Task A_client_written_before_port_options_exposes_only_plain_manual_ports()
  {
    ISandboxesClient client = new ManualPortsOnly();

    var manual = await client.AddPortAsync(
      Id,
      8080,
      new SandboxPortOptions { Anonymous = true },
      TestToken
    );

    await Assert.That(manual.Id).IsEqualTo(Id);
    await Assert
      .That(async () => await client.AddPortAsync(Id, 8080, OnDemand(), TestToken))
      .Throws<NotSupportedException>();
    await Assert
      .That(async () =>
        await client.AddPortAsync(
          Id,
          8080,
          new SandboxPortOptions { Anonymous = true, AllowedSourceCidrs = ["203.0.113.7/32"] },
          TestToken
        )
      )
      .Throws<NotSupportedException>();
    await Assert
      .That(async () => await client.DisableAsync(Id, TestToken))
      .Throws<NotSupportedException>();
    await Assert
      .That(async () => await client.EnableAsync(Id, TestToken))
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
          OnDemand() with
          {
            Activation = (SandboxPortActivation)7,
          },
          TestToken
        )
      )
      .Throws<ArgumentException>();
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

    await Assert.That(plane.Requests.Single().Uri.ToString()).IsEqualTo(FirstPage);
  }

  /// <summary>A page of the listing on the paged API version, with its next link if any.</summary>
  private static string Page(string? nextLink, params string[] ids) =>
    "{\"value\": ["
    + string.Join(", ", ids.Select(id => FakeDataPlane.Sandbox(id, "Running")))
    + "]"
    + (nextLink is null ? "" : $", \"nextLink\": \"{nextLink}\"")
    + "}";

  private static SandboxSpec Spec() =>
    new()
    {
      DiskImageId = "disk-1",
      Cpu = "500m",
      Memory = "1024Mi",
      Entrypoint = ["/bin/sleep", "infinity"],
    };

  /// <summary>An anonymous on-demand port, as the provisioner exposes renderers.</summary>
  private static SandboxPortOptions OnDemand() =>
    new() { Anonymous = true, Activation = SandboxPortActivation.OnDemand };

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
