using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Hosting.Tests.Support;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Hosting.Tests.Sandboxes;

/// <summary>
/// The client's access tokens: one reused until five minutes before it expires, refreshed once for
/// every caller that finds it stale, and kept through a failed refresh while it lasts.
/// </summary>
public class AccessTokenTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task A_token_is_reused_until_five_minutes_before_it_expires()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("[]"));

    await sandboxes.Client.ListAsync(TestToken);
    sandboxes.Clock.Advance(TimeSpan.FromMinutes(55) - TestClock.Tick);
    await sandboxes.Client.ListAsync(TestToken);
    sandboxes.Clock.Advance(TestClock.Tick);
    await sandboxes.Client.ListAsync(TestToken);
    await sandboxes.Client.ListAsync(TestToken);

    await Assert.That(sandboxes.Credential.Requests).IsEqualTo(2);
    await Assert
      .That(sandboxes.Plane.Requests.Select(request => request.Authorization!))
      .IsEquivalentTo(
        ["Bearer token-1", "Bearer token-1", "Bearer token-2", "Bearer token-2"],
        CollectionOrdering.Matching
      );
  }

  [Test]
  public async Task Concurrent_callers_share_one_refresh()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("[]"));
    TaskCompletionSource issue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    sandboxes.Credential.Gate = issue.Task;

    var lists = Enumerable.Range(0, 8).Select(_ => sandboxes.Client.ListAsync(TestToken)).ToList();
    await Task.Delay(TimeSpan.FromMilliseconds(50), TestToken);
    issue.SetResult();
    await Task.WhenAll(lists);

    await Assert.That(sandboxes.Credential.Requests).IsEqualTo(1);
    await Assert
      .That(sandboxes.Plane.Requests.All(request => request.Authorization == "Bearer token-1"))
      .IsTrue();
  }

  [Test]
  public async Task A_failed_refresh_keeps_the_token_while_it_lasts()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("[]"));
    await sandboxes.Client.ListAsync(TestToken);
    sandboxes.Clock.Advance(TimeSpan.FromMinutes(57));
    sandboxes.Credential.Failure = new InvalidOperationException("The endpoint is down.");

    await sandboxes.Client.ListAsync(TestToken);

    await Assert.That(sandboxes.Credential.Requests).IsEqualTo(2);
    await Assert.That(sandboxes.Plane.Requests[1].Authorization).IsEqualTo("Bearer token-1");

    // Once it has expired, the failure stands.
    sandboxes.Clock.Advance(TimeSpan.FromMinutes(3));
    await Assert
      .That(async () => await sandboxes.Client.ListAsync(TestToken))
      .Throws<SandboxesException>();
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(2);
  }

  [Test]
  public async Task A_refresh_is_tried_again_after_one_fails()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("[]"));
    sandboxes.Credential.Failure = new InvalidOperationException("Not yet.");
    await Assert
      .That(async () => await sandboxes.Client.ListAsync(TestToken))
      .Throws<SandboxesException>();

    sandboxes.Credential.Failure = null;
    await sandboxes.Client.ListAsync(TestToken);

    await Assert.That(sandboxes.Credential.Requests).IsEqualTo(2);
    await Assert.That(sandboxes.Plane.Requests.Single().Authorization).IsEqualTo("Bearer token-1");
  }

  [Test]
  public async Task A_short_lived_token_is_refreshed_halfway_through_its_life()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("[]"));
    sandboxes.Credential.Lifetime = TimeSpan.FromMinutes(4);

    await sandboxes.Client.ListAsync(TestToken);
    await sandboxes.Client.ListAsync(TestToken);
    sandboxes.Clock.Advance(TimeSpan.FromMinutes(2) - TestClock.Tick);
    await sandboxes.Client.ListAsync(TestToken);
    await Assert.That(sandboxes.Credential.Requests).IsEqualTo(1);
    sandboxes.Clock.Advance(TestClock.Tick);
    await sandboxes.Client.ListAsync(TestToken);

    await Assert.That(sandboxes.Credential.Requests).IsEqualTo(2);
    await Assert.That(sandboxes.Plane.Requests[^1].Authorization).IsEqualTo("Bearer token-2");
  }

  [Test]
  public async Task An_expired_token_is_never_reused()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("[]"));
    sandboxes.Credential.Lifetime = TimeSpan.FromMinutes(-1);

    await sandboxes.Client.ListAsync(TestToken);
    await sandboxes.Client.ListAsync(TestToken);

    await Assert.That(sandboxes.Credential.Requests).IsEqualTo(2);
  }

  [Test]
  public async Task A_caller_stops_waiting_for_a_refresh_when_canceled()
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Ok("[]"));
    TaskCompletionSource never = new();
    sandboxes.Credential.Gate = never.Task;
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);

    var list = sandboxes.Client.ListAsync(cancellation.Token);
    await cancellation.CancelAsync();

    await Assert.That(async () => await list).Throws<OperationCanceledException>();
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(0);
  }
}
