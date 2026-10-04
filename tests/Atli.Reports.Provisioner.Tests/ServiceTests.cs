using System.Net;
using System.Text.Json;
using Atli.Reports.Hosting.Provisioning;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner.Service;
using Atli.Reports.Provisioner.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>
/// The provisioning service (<c>serve</c>) over HTTP: only the gateway's keys get in, only tenants
/// under the managed prefixes are served, within their quotas and the rate limit, concurrent first
/// uses share one creation, failures are answered without their reasons, and nothing it logs holds
/// a key.
/// </summary>
public class ServiceTests
{
  private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [Arguments("none")]
  [Arguments("an unknown key")]
  [Arguments("a wrong secret")]
  [Arguments("no secret")]
  [Arguments("the key ID alone")]
  [Arguments("the key's verifier")]
  public async Task A_request_without_one_of_the_gateways_keys_is_unauthorized(string presented)
  {
    using Provisioning provisioning = new();
    await using var service = await RunningService.StartAsync(provisioning);
    var gateway = RunningService.Gateway;
    var key = presented switch
    {
      "none" => null,
      "an unknown key" => RendererCredential.Generate().Credential,
      "a wrong secret" => gateway.KeyId + "." + RendererCredential.Generate().Credential[^64..],
      "no secret" => gateway.KeyId + ".",
      "the key ID alone" => gateway.KeyId,
      _ => gateway.KeyId + "." + gateway.Verifier,
    };

    using var response = await service.SendAsync(
      HttpMethod.Put,
      "/tenants/myapp-1/renderer",
      key,
      TestToken
    );

    var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
    await Assert
      .That(problem.GetProperty("detail").GetString())
      .IsEqualTo("Send the gateway's credential in X-Reports-Api-Key.");
    await Assert.That(response.Headers.WwwAuthenticate.ToString()).IsEqualTo("ProvisioningApiKey");
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
    var refused = service.Logs.Of<GatewayAuthentication>(10);
    await Assert.That(refused).HasSingleItem();
    await Assert.That(refused[0].Level).IsEqualTo(LogLevel.Warning);
  }

  [Test]
  public async Task Either_of_two_keys_is_accepted_so_keys_can_be_rotated()
  {
    using Provisioning provisioning = new();
    await using var service = await RunningService.StartAsync(provisioning);

    using var current = await service.SendAsync(
      HttpMethod.Put,
      "/tenants/myapp-1/renderer",
      RunningService.Gateway.Credential,
      TestToken
    );
    using var next = await service.SendAsync(
      HttpMethod.Put,
      "/tenants/myapp-2/renderer",
      RunningService.Rotated.Credential,
      TestToken
    );

    await Assert.That(current.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.OK);
  }

  [Test]
  public async Task Unknown_routes_need_a_key_too()
  {
    using Provisioning provisioning = new();
    await using var service = await RunningService.StartAsync(provisioning);

    using var anonymous = await service.SendAsync(HttpMethod.Get, "/tenants", null, TestToken);
    using var unknown = await service.SendAsync(
      HttpMethod.Get,
      "/tenants",
      RunningService.Gateway.Credential,
      TestToken
    );
    using var wrongMethod = await service.SendAsync(
      HttpMethod.Get,
      "/tenants/myapp-1/renderer",
      RunningService.Gateway.Credential,
      TestToken
    );

    await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "Unauthorized");
    await AssertProblemAsync(unknown, HttpStatusCode.NotFound, "InvalidRequest");
    await AssertProblemAsync(wrongMethod, HttpStatusCode.MethodNotAllowed, "InvalidRequest");
  }

  [Test]
  public async Task The_health_probes_need_no_key()
  {
    using Provisioning provisioning = new();
    await using var service = await RunningService.StartAsync(provisioning);

    using var live = await service.SendAsync(HttpMethod.Get, "/health/live", null, TestToken);
    using var ready = await service.SendAsync(HttpMethod.Get, "/health/ready", null, TestToken);

    await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(await live.Content.ReadAsStringAsync(TestToken)).IsEqualTo("Healthy");
    await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
  }

  [Test]
  public async Task It_is_ready_only_while_the_record_store_answers()
  {
    using Provisioning provisioning = new();
    ListingRecordStore records = new(provisioning.Records)
    {
      FailListing = new InvalidOperationException("The vault is unavailable."),
    };
    await using var service = await RunningService.StartAsync(provisioning, records: records);

    using var unready = await service.SendAsync(HttpMethod.Get, "/health/ready", null, TestToken);
    using var live = await service.SendAsync(HttpMethod.Get, "/health/live", null, TestToken);
    records.FailListing = null;
    using var ready = await service.SendAsync(HttpMethod.Get, "/health/ready", null, TestToken);

    await Assert.That(unready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
  }

  [Test]
  [Arguments("PUT", "Contoso")]
  [Arguments("PUT", "-myapp-1")]
  [Arguments("PUT", "myapp_1")]
  [Arguments("PUT", "myapp-123456789012345678901234567890123456789012345678901234567890")]
  [Arguments("DELETE", "Contoso")]
  public async Task A_tenant_ID_that_is_not_one_is_a_bad_request(string method, string tenantId)
  {
    using Provisioning provisioning = new();
    await using var service = await RunningService.StartAsync(provisioning);

    using var response = await service.SendAsync(
      new HttpMethod(method),
      $"/tenants/{tenantId}/renderer",
      RunningService.Gateway.Credential,
      TestToken
    );

    await AssertProblemAsync(response, HttpStatusCode.BadRequest, "InvalidRequest");
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
  }

  [Test]
  [Arguments("PUT", "other-1")]
  [Arguments("PUT", "myapp-")]
  [Arguments("PUT", "myapp")]
  [Arguments("DELETE", "other-1")]
  public async Task A_tenant_under_none_of_the_managed_prefixes_is_not_allowed(
    string method,
    string tenantId
  )
  {
    using Provisioning provisioning = new();
    // A tenant the operator manages; the gateway can neither create nor delete it.
    var listed = provisioning.AddRenderer("other-1", "disk-1");
    await using var service = await RunningService.StartAsync(provisioning);

    using var response = await service.SendAsync(
      new HttpMethod(method),
      $"/tenants/{tenantId}/renderer",
      RunningService.Gateway.Credential,
      TestToken
    );

    var problem = await AssertProblemAsync(response, HttpStatusCode.Forbidden, "NotAllowed");
    await Assert.That(problem.GetProperty("detail").GetString()).Contains($"Tenant {tenantId} ");
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
    await Assert.That(provisioning.Records["other-1"]).IsSameReferenceAs(listed);
    var refused = service.Logs.Of<ManagedRenderers>(7);
    await Assert.That(refused).HasSingleItem();
    await Assert.That(refused[0].Level).IsEqualTo(LogLevel.Warning);
  }

  [Test]
  public async Task Creates_a_renderer_on_a_tenants_first_use()
  {
    using Provisioning provisioning = new();
    await using var service = await RunningService.StartAsync(provisioning);

    using var response = await service.PutAsync("myapp-1", TestToken);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(response.Content.Headers.ContentType?.MediaType)
      .IsEqualTo("application/json");
    await Assert
      .That(await response.Content.ReadAsStringAsync(TestToken))
      .IsEqualTo("""{"tenantId":"myapp-1","created":true}""");
    await Assert.That(provisioning.Records["myapp-1"]!.SandboxId).IsEqualTo("sandbox-1");
    var spec = provisioning.Sandboxes.Created.Single();
    await Assert.That(spec.DiskImageId).IsEqualTo("disk-1");
    // The prefix names no size, so Provisioner:Size.
    await Assert.That(spec.Labels["size"]).IsEqualTo("M");
    await Assert.That(service.Logs.Of<ManagedRenderers>(1)).HasSingleItem();
    // The provisioner's progress goes to the log, with the tenant as a property.
    await Assert
      .That(service.Logs.Of<RendererProvisioner>(30).Select(entry => entry.Message).ToArray())
      .Contains("[myapp-1] The record now points to sandbox sandbox-1.");
  }

  [Test]
  public async Task Creates_renderers_of_their_prefixs_size()
  {
    using Provisioning provisioning = new();
    await using var service = await RunningService.StartAsync(provisioning);

    using var response = await service.PutAsync("big-1", TestToken);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(provisioning.Sandboxes.Created.Single().Labels["size"]).IsEqualTo("L");
  }

  [Test]
  public async Task Finds_a_renderer_that_exists_without_counting_toward_any_limit()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("myapp-1", "disk-1");
    ListingRecordStore records = new(provisioning.Records);
    await using var service = await RunningService.StartAsync(
      provisioning,
      service => service.MaxCreatesPerMinute = 1,
      records
    );

    using var first = await service.PutAsync("myapp-1", TestToken);
    using var second = await service.PutAsync("myapp-1", TestToken);

    foreach (var response in new[] { first, second })
    {
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
      await Assert
        .That(await response.Content.ReadAsStringAsync(TestToken))
        .IsEqualTo("""{"tenantId":"myapp-1","created":false}""");
    }

    await Assert.That(provisioning.Sandboxes.Created).IsEmpty();
    // Neither the quota nor the rate limit was asked.
    await Assert.That(records.Listings).IsEqualTo(0);
    using var created = await service.PutAsync("myapp-2", TestToken);
    await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
  }

  [Test]
  public async Task Replaces_a_renderer_whose_sandbox_is_gone()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("myapp-1", "disk-1");
    provisioning.Sandboxes.Remove("old-myapp-1");
    await using var service = await RunningService.StartAsync(provisioning);

    using var response = await service.PutAsync("myapp-1", TestToken);

    await Assert
      .That(await response.Content.ReadAsStringAsync(TestToken))
      .IsEqualTo("""{"tenantId":"myapp-1","created":true}""");
    await Assert.That(provisioning.Records["myapp-1"]!.SandboxId).IsEqualTo("sandbox-1");
  }

  [Test]
  public async Task Concurrent_first_uses_of_a_tenant_share_one_creation()
  {
    using Provisioning provisioning = new();
    TaskCompletionSource probing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    provisioning.Readiness.Answer = async (_, cancellationToken) =>
    {
      probing.TrySetResult();
      await ready.Task.WaitAsync(cancellationToken);
      return ReadinessAnswer.Ready;
    };
    await using var service = await RunningService.StartAsync(provisioning);

    var first = service.PutAsync("myapp-1", TestToken);
    await probing.Task.WaitAsync(TestToken);
    var others = Enumerable
      .Range(0, 7)
      .Select(_ => service.PutAsync("myapp-1", TestToken))
      .ToArray();
    await service.Logs.WaitForAsync<ManagedRenderers>(4, count: others.Length);
    ready.SetResult();
    var responses = await Task.WhenAll([first, .. others]);

    foreach (var response in responses)
    {
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
      await Assert
        .That(await response.Content.ReadAsStringAsync(TestToken))
        .IsEqualTo("""{"tenantId":"myapp-1","created":true}""");
      response.Dispose();
    }

    await Assert.That(provisioning.Sandboxes.Created).HasSingleItem();
    await Assert
      .That(provisioning.Journal.Matching("put"))
      .IsEquivalentTo(["put myapp-1 -> sandbox-1"]);
  }

  [Test]
  public async Task A_creation_another_replica_finished_first_is_answered_as_found()
  {
    using Provisioning provisioning = new();
    var other = provisioning.AddRenderer("myapp-1", "disk-1");
    await provisioning.Records.DeleteAsync("myapp-1", TestToken);
    provisioning.Readiness.Answer = (_, _) =>
    {
      provisioning.Records.Add(other);
      return Task.FromResult(ReadinessAnswer.Ready);
    };
    await using var service = await RunningService.StartAsync(provisioning);

    using var response = await service.PutAsync("myapp-1", TestToken);

    await Assert
      .That(await response.Content.ReadAsStringAsync(TestToken))
      .IsEqualTo("""{"tenantId":"myapp-1","created":false}""");
    await Assert.That(provisioning.Records["myapp-1"]).IsSameReferenceAs(other);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-myapp-1"]);
  }

  [Test]
  public async Task A_record_that_names_another_tenants_sandbox_is_a_failure_not_found()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("myapp-b", "disk-1");
    var tampered = provisioning.AddRenderer("myapp-a", "disk-1") with { SandboxId = "old-myapp-b" };
    provisioning.Records.Add(tampered);
    await using var service = await RunningService.StartAsync(provisioning);

    using var response = await service.PutAsync("myapp-a", TestToken);

    await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Failed");
    var failures = service.Logs.Of<ManagedRenderers>(2);
    await Assert.That(failures).HasSingleItem();
    await Assert.That(failures[0].Exception!.Message).Contains("labeled for tenant myapp-b");
    await Assert.That(provisioning.Records["myapp-a"]).IsSameReferenceAs(tampered);
    await Assert.That(provisioning.Sandboxes.Created).IsEmpty();
  }

  [Test]
  public async Task A_prefix_with_its_most_renderers_refuses_only_new_tenants()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("myapp-a", "disk-1");
    provisioning.AddRenderer("myapp-b", "disk-1");
    provisioning.Sandboxes.Remove("old-myapp-b");
    await using var service = await RunningService.StartAsync(
      provisioning,
      service => service.TenantPrefixes[0].MaxTenants = 2
    );

    using var refused = await service.PutAsync("myapp-c", TestToken);
    using var found = await service.PutAsync("myapp-a", TestToken);
    // Replacing a sandbox that is gone adds no renderer.
    using var replaced = await service.PutAsync("myapp-b", TestToken);
    // Each prefix has a quota of its own.
    using var elsewhere = await service.PutAsync("big-1", TestToken);

    await AssertProblemAsync(refused, HttpStatusCode.TooManyRequests, "QuotaExceeded");
    await Assert.That(refused.Headers.RetryAfter).IsNull();
    await Assert.That(found.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(elsewhere.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(provisioning.Sandboxes.Created.Select(spec => spec.Labels["tenant"]))
      .IsEquivalentTo(["myapp-b", "big-1"]);
    await Assert.That(service.Logs.Of<ManagedRenderers>(5)).HasSingleItem();
  }

  [Test]
  public async Task Creations_in_flight_count_toward_the_quota_and_a_delete_frees_a_place()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("myapp-a", "disk-1");
    TaskCompletionSource probing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    provisioning.Readiness.Answer = async (_, cancellationToken) =>
    {
      probing.TrySetResult();
      await ready.Task.WaitAsync(cancellationToken);
      return ReadinessAnswer.Ready;
    };
    ListingRecordStore records = new(provisioning.Records);
    await using var service = await RunningService.StartAsync(
      provisioning,
      service => service.TenantPrefixes[0].MaxTenants = 2,
      records
    );

    var creating = service.PutAsync("myapp-b", TestToken);
    await probing.Task.WaitAsync(TestToken);
    using var whileCreating = await service.PutAsync("myapp-c", TestToken);
    ready.SetResult();
    using var created = await creating;
    // The store was listed before myapp-b had a record; the service counts it all the same.
    using var afterCreating = await service.PutAsync("myapp-c", TestToken);
    using var deleted = await service.DeleteAsync("myapp-a", TestToken);
    using var afterDeleting = await service.PutAsync("myapp-c", TestToken);

    await AssertProblemAsync(whileCreating, HttpStatusCode.TooManyRequests, "QuotaExceeded");
    await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await AssertProblemAsync(afterCreating, HttpStatusCode.TooManyRequests, "QuotaExceeded");
    await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    await Assert.That(afterDeleting.StatusCode).IsEqualTo(HttpStatusCode.OK);
    // One listing served every count: it is kept for TenantCensus.MaxAge.
    await Assert.That(records.Listings).IsEqualTo(1);
  }

  [Test]
  public async Task Records_written_elsewhere_count_once_the_store_is_listed_again()
  {
    using Provisioning provisioning = new();
    ListingRecordStore records = new(provisioning.Records);
    await using var service = await RunningService.StartAsync(
      provisioning,
      service => service.TenantPrefixes[0].MaxTenants = 2,
      records
    );

    using var first = await service.PutAsync("myapp-a", TestToken);
    // From the command line, say: the service learns of it from the next listing.
    provisioning.AddRenderer("myapp-x", "disk-1");
    using var second = await service.PutAsync("myapp-b", TestToken);
    provisioning.Clock.Advance(TenantCensus.MaxAge);
    using var third = await service.PutAsync("myapp-c", TestToken);

    await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await AssertProblemAsync(third, HttpStatusCode.TooManyRequests, "QuotaExceeded");
    await Assert.That(records.Listings).IsEqualTo(2);
  }

  [Test]
  public async Task Creates_beyond_the_rate_limit_are_refused_until_the_minute_has_passed()
  {
    using Provisioning provisioning = new();
    await using var service = await RunningService.StartAsync(
      provisioning,
      service => service.MaxCreatesPerMinute = 2
    );

    using var first = await service.PutAsync("myapp-1", TestToken);
    using var second = await service.PutAsync("big-2", TestToken);
    using var refused = await service.PutAsync("myapp-3", TestToken);
    provisioning.Clock.Advance(TimeSpan.FromSeconds(45));
    using var stillRefused = await service.PutAsync("myapp-3", TestToken);
    // Finding a renderer creates nothing, so it is never limited.
    using var found = await service.PutAsync("myapp-1", TestToken);
    provisioning.Clock.Advance(TimeSpan.FromSeconds(15));
    using var allowed = await service.PutAsync("myapp-3", TestToken);

    await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await AssertProblemAsync(refused, HttpStatusCode.TooManyRequests, "RateLimited");
    await Assert.That(refused.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(60));
    await AssertProblemAsync(stillRefused, HttpStatusCode.TooManyRequests, "RateLimited");
    await Assert.That(stillRefused.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(15));
    await Assert.That(found.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(provisioning.Sandboxes.Created.Count).IsEqualTo(3);
  }

  [Test]
  [Arguments("an unreadable record", "The record of myapp-1 is damaged.")]
  [Arguments("the data plane", "Too many ports.")]
  [Arguments("the record store", "The vault is unavailable.")]
  public async Task A_failure_is_503_and_logged_with_a_reason_the_answer_leaves_out(
    string failing,
    string reason
  )
  {
    using Provisioning provisioning = new();
    ListingRecordStore records = new(provisioning.Records);
    switch (failing)
    {
      case "an unreadable record":
        provisioning.Records.AddUnreadable("myapp-1");
        break;
      case "the data plane":
        provisioning.Sandboxes.FailAddPort = _ => new SandboxesException(
          reason,
          HttpStatusCode.Conflict
        );
        break;
      default:
        records.FailListing = new InvalidOperationException(reason);
        break;
    }

    await using var service = await RunningService.StartAsync(provisioning, records: records);

    using var response = await service.PutAsync("myapp-1", TestToken);

    var problem = await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Failed");
    await Assert.That(problem.ToString()).DoesNotContain(reason);
    await Assert.That(response.Headers.RetryAfter).IsNull();
    var failures = service.Logs.Of<ManagedRenderers>(2);
    await Assert.That(failures).HasSingleItem();
    await Assert.That(failures[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(failures[0].Message).Contains("myapp-1");
    await Assert.That(failures[0].Exception!.Message).Contains(reason);
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
  }

  [Test]
  public async Task A_failed_creation_can_be_asked_for_again()
  {
    using Provisioning provisioning = new();
    provisioning.Sandboxes.FailAddPort = _ => new SandboxesException("Too many ports.");
    await using var service = await RunningService.StartAsync(provisioning);

    using var failed = await service.PutAsync("myapp-1", TestToken);
    provisioning.Sandboxes.FailAddPort = null;
    using var retried = await service.PutAsync("myapp-1", TestToken);

    await AssertProblemAsync(failed, HttpStatusCode.ServiceUnavailable, "Failed");
    await Assert
      .That(await retried.Content.ReadAsStringAsync(TestToken))
      .IsEqualTo("""{"tenantId":"myapp-1","created":true}""");
  }

  [Test]
  public async Task Deletes_a_tenants_renderer_and_record_as_often_as_asked()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("myapp-1", "disk-1");
    provisioning.AddRenderer("myapp-2", "disk-1");
    await using var service = await RunningService.StartAsync(provisioning);

    using var deleted = await service.DeleteAsync("myapp-1", TestToken);
    using var again = await service.DeleteAsync("myapp-1", TestToken);
    using var never = await service.DeleteAsync("myapp-3", TestToken);

    foreach (var response in new[] { deleted, again, never })
    {
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
      await Assert.That(await response.Content.ReadAsStringAsync(TestToken)).IsEmpty();
    }

    await Assert.That(provisioning.Records["myapp-1"]).IsNull();
    await Assert.That(provisioning.Records["myapp-2"]).IsNotNull();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-myapp-2"]);
  }

  [Test]
  public async Task A_delete_that_fails_is_503()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("myapp-1", "disk-1");
    provisioning.Sandboxes.FailDelete.Add("old-myapp-1");
    await using var service = await RunningService.StartAsync(provisioning);

    using var response = await service.DeleteAsync("myapp-1", TestToken);

    await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Failed");
    await Assert.That(service.Logs.Of<ManagedRenderers>(9)).HasSingleItem();
  }

  [Test]
  public async Task A_delete_waits_for_the_tenants_creation_in_flight()
  {
    using Provisioning provisioning = new();
    TaskCompletionSource probing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    provisioning.Readiness.Answer = async (_, cancellationToken) =>
    {
      probing.TrySetResult();
      await ready.Task.WaitAsync(cancellationToken);
      return ReadinessAnswer.Ready;
    };
    await using var service = await RunningService.StartAsync(provisioning);

    var creating = service.PutAsync("myapp-1", TestToken);
    await probing.Task.WaitAsync(TestToken);
    var deleting = service.DeleteAsync("myapp-1", TestToken);
    await Task.Delay(TimeSpan.FromMilliseconds(200), TestToken);
    await Assert.That(deleting.IsCompleted).IsFalse();
    ready.SetResult();
    using var created = await creating;
    using var deleted = await deleting;

    await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    // Had the delete gone first, the record would now point to a deleted sandbox.
    await Assert.That(provisioning.Records["myapp-1"]).IsNull();
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
  }

  [Test]
  public async Task Stopping_cancels_creations_in_flight_which_delete_their_sandboxes()
  {
    using Provisioning provisioning = new();
    TaskCompletionSource probing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    provisioning.Readiness.Answer = async (_, cancellationToken) =>
    {
      probing.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      return ReadinessAnswer.Ready;
    };
    await using var service = await RunningService.StartAsync(provisioning);

    var creating = service.PutAsync("myapp-1", TestToken);
    await probing.Task.WaitAsync(TestToken);
    var exitCode = await service.StopAsync();
    using var response = await creating;

    await Assert.That(exitCode).IsEqualTo(0);
    await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Failed");
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
    await Assert.That(provisioning.Records.Count).IsEqualTo(0);
    await Assert.That(service.Logs.Of<ManagedRenderers>(3)).HasSingleItem();
  }

  [Test]
  public async Task Retires_idle_renderers_one_interval_after_starting_and_every_interval_after()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("myapp-1", "disk-1");
    provisioning.AddRenderer("myapp-2", "disk-1");
    provisioning.Sandboxes.Suspend("old-myapp-1", TimeSpan.FromDays(8));
    await using var service = await RunningService.StartAsync(provisioning);

    await provisioning.Clock.WaitForTimerAsync(Hour);
    provisioning.Clock.Advance(Hour - TestClock.Tick);
    await Assert.That(provisioning.Records["myapp-1"]).IsNotNull();
    provisioning.Clock.Advance(TestClock.Tick);
    await service.Logs.WaitForAsync<RetirementLoop>(20);
    await Assert.That(provisioning.Records["myapp-1"]).IsNull();
    await Assert.That(provisioning.Records["myapp-2"]).IsNotNull();

    provisioning.Sandboxes.Suspend("old-myapp-2", TimeSpan.FromDays(8));
    await provisioning.Clock.WaitForTimerAsync(Hour);
    provisioning.Clock.Advance(Hour);
    await service.Logs.WaitForAsync<RetirementLoop>(20, count: 2);

    await Assert.That(provisioning.Records.Count).IsEqualTo(0);
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
  }

  [Test]
  public async Task Nothing_the_service_logs_holds_a_key()
  {
    using Provisioning provisioning = new();
    provisioning.Records.AddUnreadable("myapp-9");
    await using var service = await RunningService.StartAsync(provisioning);
    var gateway = RunningService.Gateway;
    var wrongSecret = gateway.KeyId + "." + RendererCredential.Generate().Credential[^64..];
    var intruder = RendererCredential.Generate();

    using var created = await service.PutAsync("myapp-1", TestToken);
    var renderer = provisioning.Records["myapp-1"]!.ApiKey;
    using var rotated = await service.SendAsync(
      HttpMethod.Put,
      "/tenants/myapp-2/renderer",
      RunningService.Rotated.Credential,
      TestToken
    );
    using var wrong = await service.SendAsync(
      HttpMethod.Put,
      "/tenants/myapp-1/renderer",
      wrongSecret,
      TestToken
    );
    using var unknown = await service.SendAsync(
      HttpMethod.Delete,
      "/tenants/myapp-1/renderer",
      intruder.Credential,
      TestToken
    );
    using var failed = await service.PutAsync("myapp-9", TestToken);
    using var deleted = await service.DeleteAsync("myapp-1", TestToken);
    await service.StopAsync();

    await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    var logged = service.Logs.Text;
    // Something was logged, the request pipeline's own entries included.
    await Assert.That(logged).Contains("myapp-1");
    await Assert.That(logged).Contains("Request starting");
    foreach (
      var secret in new[]
      {
        gateway.Credential,
        gateway.Credential[^64..],
        gateway.Verifier,
        RunningService.Rotated.Credential,
        RunningService.Rotated.Credential[^64..],
        RunningService.Rotated.Verifier,
        wrongSecret[^64..],
        intruder.Credential[^64..],
        renderer,
        renderer[^64..],
        RendererCredential.VerifierOf(renderer),
      }
    )
    {
      await Assert.That(logged).DoesNotContain(secret);
    }
  }

  [Test]
  public async Task The_gateways_client_understands_every_answer()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("myapp-found", "disk-1");
    provisioning.AddRenderer("myapp-other", "disk-1");
    await using var service = await RunningService.StartAsync(
      provisioning,
      service =>
      {
        service.TenantPrefixes[0].MaxTenants = 3;
        service.TenantPrefixes[1].MaxTenants = 2;
        service.MaxCreatesPerMinute = 2;
      }
    );
    using var handler = ProvisioningClient.CreateHandler();
    using HttpClient http = new(handler);
    ProvisioningClient client = new(
      http,
      service.Client.BaseAddress!,
      RunningService.Gateway.Credential
    );
    ProvisioningClient intruder = new(
      http,
      service.Client.BaseAddress!,
      RendererCredential.Generate().Credential
    );

    var found = await client.EnsureRendererAsync("myapp-found", TestToken);
    var created = await client.EnsureRendererAsync("myapp-new", TestToken);
    var quota = await RefusalAsync(() => client.EnsureRendererAsync("myapp-over", TestToken));
    await client.EnsureRendererAsync("big-1", TestToken);
    var rate = await RefusalAsync(() => client.EnsureRendererAsync("big-2", TestToken));
    var notAllowed = await RefusalAsync(() => client.EnsureRendererAsync("other-1", TestToken));
    var unauthorized = await RefusalAsync(() =>
      intruder.EnsureRendererAsync("myapp-found", TestToken)
    );
    await client.DeleteRendererAsync("myapp-other", TestToken);
    await client.DeleteRendererAsync("myapp-other", TestToken);
    provisioning.Records.AddUnreadable("myapp-broken");
    var failed = await RefusalAsync(() => client.EnsureRendererAsync("myapp-broken", TestToken));

    await Assert.That(found).IsFalse();
    await Assert.That(created).IsTrue();
    await Assert
      .That((quota.StatusCode, quota.Kind, quota.RetryAfter))
      .IsEqualTo((429, "QuotaExceeded", (TimeSpan?)null));
    await Assert.That((rate.StatusCode, rate.Kind)).IsEqualTo((429, "RateLimited"));
    await Assert.That(rate.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(60));
    await Assert.That((notAllowed.StatusCode, notAllowed.Kind)).IsEqualTo((403, "NotAllowed"));
    await Assert.That(unauthorized.StatusCode).IsEqualTo(401);
    await Assert.That((failed.StatusCode, failed.Kind)).IsEqualTo((503, "Failed"));
    // The reason, that the record is damaged, stays in the service's log.
    await Assert.That(failed.Message).DoesNotContain("damaged");
    await Assert.That(provisioning.Records["myapp-other"]).IsNull();
  }

  [Test]
  public async Task The_retirement_loop_runs_one_interval_after_starting_then_one_after_each_run()
  {
    using Provisioning provisioning = new();
    using LogCapture logs = new();
    using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
    var runs = 0;
    var run = new SemaphoreSlim(0);
    using var census = new TenantCensus(provisioning.Records, provisioning.Clock);
    using RetirementLoop loop = new(
      new ProvisioningServiceOptions(),
      _ =>
      {
        Interlocked.Increment(ref runs);
        run.Release();
        return Task.FromResult(Retired());
      },
      census,
      provisioning.Clock,
      loggers.CreateLogger<RetirementLoop>()
    );

    await loop.StartAsync(TestToken);
    await provisioning.Clock.WaitForTimerAsync(Hour);
    provisioning.Clock.Advance(Hour - TestClock.Tick);
    await Assert.That(Volatile.Read(ref runs)).IsEqualTo(0);
    provisioning.Clock.Advance(TestClock.Tick);
    await run.WaitAsync(TestToken);
    await provisioning.Clock.WaitForTimerAsync(Hour);
    provisioning.Clock.Advance(Hour);
    await run.WaitAsync(TestToken);
    await loop.StopAsync(TestToken);

    await Assert.That(runs).IsEqualTo(2);
    await Assert.That(logs.Of<RetirementLoop>(20)).Count().IsEqualTo(2);
    run.Dispose();
  }

  [Test]
  public async Task A_failed_retirement_run_is_logged_and_the_next_one_runs_as_planned()
  {
    using Provisioning provisioning = new();
    using LogCapture logs = new();
    using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
    var runs = 0;
    using var census = new TenantCensus(provisioning.Records, provisioning.Clock);
    using RetirementLoop loop = new(
      new ProvisioningServiceOptions(),
      _ =>
        Interlocked.Increment(ref runs) switch
        {
          1 => throw new InvalidOperationException("The data plane is unavailable."),
          2 => Task.FromException<RetireResult>(new TimeoutException("No answer.")),
          _ => Task.FromResult(Retired(failures: new() { ["myapp-1"] = "it was busy." })),
        },
      census,
      provisioning.Clock,
      loggers.CreateLogger<RetirementLoop>()
    );

    await loop.StartAsync(TestToken);
    for (var expected = 1; expected <= 3; expected++)
    {
      await provisioning.Clock.WaitForTimerAsync(Hour);
      provisioning.Clock.Advance(Hour);
    }

    await logs.WaitForAsync<RetirementLoop>(21);
    await loop.StopAsync(TestToken);

    var failures = logs.Of<RetirementLoop>(22);
    await Assert
      .That(failures.Select(entry => entry.Exception!.Message).ToArray())
      .IsEquivalentTo(["The data plane is unavailable.", "No answer."]);
    await Assert.That(failures[0].Level).IsEqualTo(LogLevel.Error);
    await Assert
      .That(logs.Of<RetirementLoop>(21).Single().Message)
      .IsEqualTo("Could not retire the renderer of tenant myapp-1: it was busy.");
    await Assert.That(loop.ExecuteTask!.IsFaulted).IsFalse();
  }

  [Test]
  public async Task Nothing_is_retired_when_RetireAfterIdle_is_zero()
  {
    using Provisioning provisioning = new();
    using LogCapture logs = new();
    using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
    var runs = 0;
    using var census = new TenantCensus(provisioning.Records, provisioning.Clock);
    using RetirementLoop loop = new(
      new ProvisioningServiceOptions { RetireAfterIdle = TimeSpan.Zero },
      _ =>
      {
        Interlocked.Increment(ref runs);
        return Task.FromResult(Retired());
      },
      census,
      provisioning.Clock,
      loggers.CreateLogger<RetirementLoop>()
    );

    await loop.StartAsync(TestToken);
    await loop.ExecuteTask!.WaitAsync(TestToken);
    provisioning.Clock.Advance(TimeSpan.FromDays(1));
    await loop.StopAsync(TestToken);

    await Assert.That(runs).IsEqualTo(0);
    await Assert.That(logs.Of<RetirementLoop>(23)).HasSingleItem();
  }

  private static RetireResult Retired(Dictionary<string, string>? failures = null) =>
    new([], failures ?? []);

  /// <summary>The <see cref="ProvisioningApiException"/> that <paramref name="call"/> throws.</summary>
  private static async Task<ProvisioningApiException> RefusalAsync(Func<Task> call)
  {
    try
    {
      await call();
    }
    catch (ProvisioningApiException exception)
    {
      return exception;
    }

    throw new InvalidOperationException("The call succeeded.");
  }

  /// <summary>Checks a problem details answer and returns its body.</summary>
  private static async Task<JsonElement> AssertProblemAsync(
    HttpResponseMessage response,
    HttpStatusCode status,
    string kind
  )
  {
    await Assert.That(response.StatusCode).IsEqualTo(status);
    await Assert
      .That(response.Content.Headers.ContentType?.MediaType)
      .IsEqualTo("application/problem+json");
    var problem = await RunningService.ReadJsonAsync(response);
    await Assert.That(problem.GetProperty("status").GetInt32()).IsEqualTo((int)status);
    await Assert.That(problem.GetProperty("kind").GetString()).IsEqualTo(kind);
    return problem;
  }
}
