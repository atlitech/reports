using System.Net;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Hosting.Tests.Support;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Hosting.Tests.Sandboxes;

/// <summary>
/// What the client retries, how long it waits first, and that it never retries a create. Waits run
/// on the test clock, which moves only when a test advances it.
/// </summary>
public class SandboxesClientRetryTests
{
  private const string Id = "98c01b65-b81b-4dca-b000-fdae0eb0939c";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [Arguments(HttpStatusCode.TooManyRequests)]
  [Arguments(HttpStatusCode.BadGateway)]
  [Arguments(HttpStatusCode.ServiceUnavailable)]
  [Arguments(HttpStatusCode.GatewayTimeout)]
  public async Task A_transient_answer_is_retried_after_a_second(HttpStatusCode status)
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Status(status),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running"))
    );

    var get = sandboxes.Client.GetAsync(Id, TestToken);
    var delay = await sandboxes.Clock.NextTimerAsync(TestToken);
    await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(1));
    sandboxes.Clock.Advance(delay - TestClock.Tick);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(1);
    sandboxes.Clock.Advance(TestClock.Tick);
    var sandbox = await get;

    await Assert.That(sandbox!.Id).IsEqualTo(Id);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(2);
  }

  [Test]
  public async Task A_retry_waits_exactly_the_retry_after_delay()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.TooManyRequests, "Throttled", "Slow down", "7"),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running"))
    );

    var resume = sandboxes.Client.ResumeAsync(Id, TestToken);
    var delay = await sandboxes.Clock.NextTimerAsync(TestToken);
    await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(7));
    sandboxes.Clock.Advance(delay - TestClock.Tick);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(1);
    sandboxes.Clock.Advance(TestClock.Tick);
    var resumed = await resume;

    await Assert.That(resumed.State).IsEqualTo(SandboxStates.Running);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(2);
  }

  [Test]
  public async Task A_retry_after_date_is_waited_for()
  {
    // The test clock starts at midnight on 2000-01-01, UTC.
    var at = new DateTimeOffset(2000, 1, 1, 0, 0, 12, TimeSpan.Zero).ToString(
      "R",
      System.Globalization.CultureInfo.InvariantCulture
    );
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.ServiceUnavailable, "Busy", "Later", at),
      FakeDataPlane.Status(HttpStatusCode.NoContent)
    );

    var delete = sandboxes.Client.DeleteAsync(Id, TestToken);
    var delay = await sandboxes.Clock.NextTimerAsync(TestToken);
    sandboxes.Clock.Advance(delay);
    await delete;

    await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(12));
  }

  [Test]
  public async Task A_long_retry_after_is_cut_to_the_longest_wait()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.ServiceUnavailable, "Busy", "Later", "3600"),
      FakeDataPlane.Ok("[]")
    );

    var list = sandboxes.Client.ListAsync(TestToken);
    var delay = await sandboxes.Clock.NextTimerAsync(TestToken);
    sandboxes.Clock.Advance(delay);
    await list;

    await Assert.That(delay).IsEqualTo(SandboxesClient.MaxRetryDelay);
  }

  [Test]
  public async Task Retries_back_off_and_stop_after_three()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.ServiceUnavailable, "Busy", "Try again")
    );

    var stop = sandboxes.Client.StopAsync(Id, TestToken);
    List<TimeSpan> delays = [];
    for (var retry = 0; retry < SandboxesClient.MaxRetries; retry++)
    {
      var delay = await sandboxes.Clock.NextTimerAsync(TestToken);
      delays.Add(delay);
      sandboxes.Clock.Advance(delay);
    }

    var exception = await Assert.That(async () => await stop).Throws<SandboxesException>();

    await Assert
      .That(delays)
      .IsEquivalentTo(
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
        CollectionOrdering.Matching
      );
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(SandboxesClient.MaxRetries + 1);
    await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    await Assert.That(exception.Message).Contains("Busy: Try again");
  }

  [Test]
  public async Task A_transport_failure_is_retried()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Unreachable(),
      FakeDataPlane.Ok($$"""{ "ports": [{{FakeDataPlane.Port(Id, 8080)}}] }"""),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    var add = sandboxes.Client.AddPortAsync(Id, 8080, anonymous: true, TestToken);
    sandboxes.Clock.Advance(await sandboxes.Clock.NextTimerAsync(TestToken));
    var sandbox = await add;

    await Assert.That(sandbox.Ports.Single().Port).IsEqualTo(8080);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(3);
  }

  [Test]
  public async Task A_timeout_of_the_http_client_is_retried()
  {
    var attempts = 0;
    using TestSandboxes sandboxes = new(_ =>
      Interlocked.Increment(ref attempts) == 1
        ? throw new TaskCanceledException("The request was canceled due to the configured timeout.")
        : new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent(FakeDataPlane.Sandbox(Id, "Running")),
        }
    );

    var get = sandboxes.Client.GetAsync(Id, TestToken);
    sandboxes.Clock.Advance(await sandboxes.Clock.NextTimerAsync(TestToken));
    var sandbox = await get;

    await Assert.That(sandbox!.State).IsEqualTo(SandboxStates.Running);
    await Assert.That(attempts).IsEqualTo(2);
  }

  [Test]
  [Arguments(HttpStatusCode.ServiceUnavailable)]
  [Arguments(HttpStatusCode.TooManyRequests)]
  public async Task A_create_is_never_retried(HttpStatusCode status)
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(status, "Busy", "Try again", "0"),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running"))
    );

    var exception = await Assert
      .That(async () => await sandboxes.Client.CreateAsync(Spec(), TestToken))
      .Throws<SandboxesException>();

    await Assert.That(exception!.StatusCode).IsEqualTo(status);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(1);
    await Assert.That(sandboxes.Clock.HasPendingTimer).IsFalse();
  }

  [Test]
  public async Task A_create_that_never_got_an_answer_is_not_retried()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Unreachable(),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running"))
    );

    await Assert
      .That(async () => await sandboxes.Client.CreateAsync(Spec(), TestToken))
      .Throws<SandboxesException>();

    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(1);
  }

  [Test]
  [Arguments(HttpStatusCode.BadRequest)]
  [Arguments(HttpStatusCode.Unauthorized)]
  [Arguments(HttpStatusCode.Forbidden)]
  [Arguments(HttpStatusCode.InternalServerError)]
  public async Task Answers_another_attempt_cannot_fix_are_not_retried(HttpStatusCode status)
  {
    using TestSandboxes sandboxes = new(FakeDataPlane.Problem(status, "No", "Not this way"));

    var exception = await Assert
      .That(async () => await sandboxes.Client.GetAsync(Id, TestToken))
      .Throws<SandboxesException>();

    await Assert.That(exception!.StatusCode).IsEqualTo(status);
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Cancellation_during_a_retry_wait_cancels_the_call()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Problem(HttpStatusCode.ServiceUnavailable, "Busy", "Later", "10")
    );
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);

    var get = sandboxes.Client.GetAsync(Id, cancellation.Token);
    await sandboxes.Clock.NextTimerAsync(TestToken);
    await cancellation.CancelAsync();

    await Assert.That(async () => await get).Throws<OperationCanceledException>();
    await Assert.That(sandboxes.Plane.Requests.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Every_attempt_is_a_new_request_with_the_body()
  {
    using TestSandboxes sandboxes = new(
      FakeDataPlane.Status(HttpStatusCode.BadGateway),
      FakeDataPlane.Ok($$"""{ "ports": [{{FakeDataPlane.Port(Id, 8080)}}] }"""),
      FakeDataPlane.Ok(FakeDataPlane.Sandbox(Id, "Running", $"[{FakeDataPlane.Port(Id, 8080)}]"))
    );

    var add = sandboxes.Client.AddPortAsync(Id, 8080, anonymous: true, TestToken);
    sandboxes.Clock.Advance(await sandboxes.Clock.NextTimerAsync(TestToken));
    await add;

    var requests = sandboxes.Plane.Requests;
    await Assert.That(requests[1].Body).IsEqualTo(requests[0].Body);
    await Assert.That(requests[1].Authorization).IsEqualTo("Bearer token-1");
  }

  private static SandboxSpec Spec() =>
    new()
    {
      DiskImageId = "disk-1",
      Cpu = "500m",
      Memory = "1024Mi",
      Entrypoint = ["/bin/sleep", "infinity"],
    };
}
