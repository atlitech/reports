using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Hosting.Sandboxes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using static Atli.Reports.Engine.Tests.Server.Gateway.GatewayHost;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>
/// Waking suspended Azure Container Apps sandboxes: the platform's proxy answers
/// <c>403 {"error":"Sandbox is not running"}</c> until the gateway resumes the sandbox through the
/// Sandboxes data plane, and the gateway then sends the conversion again. The answer reaches the
/// gateway through the renderer's port, so the gateway checks the sandbox's state before it resumes.
/// </summary>
public class GatewayWakeTests
{
  private const string SandboxId = "8c1f9e02-5d1b-4bb2-9a4e-3f0d6c2b7a11";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task A_suspended_renderer_is_resumed_and_the_conversion_resent()
  {
    SleepingRenderer platform = new();
    // Right after the resume the proxy has no route yet: one 502, which is retried too.
    FakeSandboxesClient sandboxes = new(
      _ => platform.State,
      (_, _) => platform.Wake(startingAnswers: 1)
    );
    await using var under = await StartAsync(platform, sandboxes, SandboxesWake());

    using var response = await under.PostAsync();

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(await response.Content.ReadAsStringAsync(TestToken))
      .IsEqualTo("%PDF-1.7 awake");
    await Assert.That(sandboxes.Gets).IsEqualTo(1);
    await Assert.That(sandboxes.Resumes).IsEqualTo(1);
    // Asleep, starting, awake: three complete requests, each with the renderer's key and the body.
    await Assert.That(under.Renderer.Requests.Count).IsEqualTo(3);
    foreach (var request in under.Renderer.Requests)
    {
      await Assert.That(request.Headers["X-Reports-Api-Key"]).IsEqualTo(TestKey);
      await Assert.That(request.Body).Contains("\"html\":\"<p>x</p>\"");
    }

    var resumed = under.Logs.WithEventId(61).Single();
    await Assert.That(resumed["SandboxId"]).IsEqualTo(SandboxId);
    await Assert.That(resumed["TenantId"]).IsEqualTo("acme");
  }

  [Test]
  public async Task Concurrent_requests_share_one_check_and_one_resume()
  {
    const int requests = 5;
    SleepingRenderer platform = new();
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    FakeSandboxesClient sandboxes = new(
      _ => platform.State,
      async (_, cancellationToken) =>
      {
        await release.Task.WaitAsync(cancellationToken);
        await platform.Wake();
      }
    );
    await using var under = await StartAsync(platform, sandboxes, SandboxesWake());

    var sent = Enumerable.Range(0, requests).Select(_ => under.PostAsync()).ToArray();
    // Every request has found the renderer asleep while the one resume is still in flight.
    var allAsleep = await TestEngine.EventuallyAsync(
      () => Task.FromResult(platform.NotRunningAnswers >= requests),
      TestEngine.GenerousTimeout
    );
    release.SetResult();
    var responses = await Task.WhenAll(sent);

    await Assert.That(allAsleep).IsTrue();
    foreach (var response in responses)
    {
      using (response)
      {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
      }
    }

    await Assert.That(sandboxes.Gets).IsEqualTo(1);
    await Assert.That(sandboxes.Resumes).IsEqualTo(1);
  }

  [Test]
  public async Task A_failed_resume_is_tried_again_no_sooner_than_five_seconds_later()
  {
    SleepingRenderer platform = new();
    var attempts = 0;
    FakeSandboxesClient sandboxes = new(
      _ => platform.State,
      (_, _) =>
      {
        if (Interlocked.Increment(ref attempts) == 1)
        {
          throw new SandboxesException("The data plane is busy.", HttpStatusCode.TooManyRequests);
        }

        platform.Wake();
        return Task.CompletedTask;
      }
    );
    await using var under = await StartAsync(platform, sandboxes, SandboxesWake());

    using var response = await under.PostAsync();

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(sandboxes.Resumes).IsEqualTo(2);
    var times = sandboxes.ResumeTimes.ToArray();
    await Assert.That(times[1] - times[0]).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(4.9));
    await Assert.That(under.Logs.WithEventId(62)).HasSingleItem();
    // Resent in between with a backoff, not in a tight loop.
    await Assert.That(platform.NotRunningAnswers).IsBetween(3, 15);
  }

  [Test]
  public async Task A_failed_state_read_is_tried_again_within_the_wake_window()
  {
    SleepingRenderer platform = new();
    var reads = 0;
    FakeSandboxesClient sandboxes = new(
      _ =>
        Interlocked.Increment(ref reads) == 1
          ? throw new SandboxesException("The data plane is down.", HttpStatusCode.BadGateway)
          : platform.State,
      (_, _) => platform.Wake()
    );
    await using var under = await StartAsync(platform, sandboxes, SandboxesWake());

    using var response = await under.PostAsync();

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(sandboxes.Gets).IsEqualTo(2);
    await Assert.That(sandboxes.Resumes).IsEqualTo(1);
    await Assert.That(under.Logs.WithEventId(63)).HasSingleItem();
  }

  [Test]
  public async Task A_renderer_that_does_not_wake_in_time_is_unavailable()
  {
    SleepingRenderer platform = new();
    // The platform takes the resume but the sandbox stays stopped.
    FakeSandboxesClient sandboxes = new(_ => platform.State, (_, _) => Task.CompletedTask);
    await using var under = await StartAsync(platform, sandboxes, SandboxesWake("00:00:01"));

    var watch = Stopwatch.StartNew();
    var problem = await ReadProblemAsync(await under.PostAsync());
    watch.Stop();

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(problem.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer did not wake in time.");
    await Assert.That(watch.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
    await Assert.That(sandboxes.Resumes).IsEqualTo(1);
    // Resent with a backoff, not in a tight loop.
    await Assert.That(platform.NotRunningAnswers).IsBetween(2, 8);
    await Assert.That(under.Logs.WithEventId(51)).HasSingleItem();
  }

  [Test]
  public async Task A_not_running_answer_from_a_running_sandbox_resumes_and_resends_nothing()
  {
    const int requests = 8;
    // The renderer is compromised and imitates the platform's answer; the sandbox is running.
    FakeSandboxesClient sandboxes = new(_ => SandboxStates.Running, (_, _) => Task.CompletedTask);
    await using var under = await GatewayUnderTest.StartAsync(
      async context =>
      {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
          """{"error":"Sandbox is not running"}""",
          context.RequestAborted
        );
      },
      SandboxesWake(),
      builder => builder.Services.AddSingleton<ISandboxesClient>(sandboxes),
      SandboxId
    );

    var watch = Stopwatch.StartNew();
    var problems = await Task.WhenAll(
      Enumerable
        .Range(0, requests)
        .Select(async _ => await ReadProblemAsync(await under.PostAsync()))
    );
    watch.Stop();

    foreach (var problem in problems)
    {
      await Assert.That(problem.Status).IsEqualTo(503);
      await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
      await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer is unavailable.");
    }

    await Assert.That(sandboxes.Resumes).IsEqualTo(0);
    // One shared state read, reused for the rest (two on a machine slow enough to outlast it).
    await Assert.That(sandboxes.Gets).IsBetween(1, 2);
    // Each request was sent once: no resends, and no waiting out the wake window.
    await Assert.That(under.Renderer.Requests.Count).IsEqualTo(requests);
    await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    await Assert.That(under.Logs.WithEventId(54).Count).IsEqualTo(requests);
  }

  [Test]
  public async Task A_sandbox_that_no_longer_exists_is_not_resumed()
  {
    SleepingRenderer platform = new();
    FakeSandboxesClient sandboxes = new(_ => null, (_, _) => platform.Wake());
    await using var under = await StartAsync(platform, sandboxes, SandboxesWake());

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer is not running.");
    await Assert.That(sandboxes.Resumes).IsEqualTo(0);
    await Assert.That(under.Renderer.Requests.Count).IsEqualTo(1);
    await Assert.That(under.Logs.WithEventId(55)).HasSingleItem();
  }

  [Test]
  public async Task A_disabled_sandbox_fails_at_once_without_a_resume()
  {
    const int requests = 4;
    // The provisioner's kill switch: the platform stops the sandbox, answers its port with
    // not-running, and refuses every resume (409 SandboxAdminDisabled) until it is enabled.
    SleepingRenderer platform = new();
    FakeSandboxesClient sandboxes = new(
      _ => platform.State,
      (_, _) =>
        throw new SandboxesException(
          "Sandboxes POST resume failed with 409 (Conflict): SandboxAdminDisabled.",
          HttpStatusCode.Conflict
        ),
      _ => SandboxStoppedReasons.Disabled
    );
    await using var under = await StartAsync(platform, sandboxes, SandboxesWake());

    var watch = Stopwatch.StartNew();
    var problems = await Task.WhenAll(
      Enumerable
        .Range(0, requests)
        .Select(async _ => await ReadProblemAsync(await under.PostAsync()))
    );
    watch.Stop();

    foreach (var problem in problems)
    {
      await Assert.That(problem.Status).IsEqualTo(503);
      await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
      await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer is not running.");
    }

    // Not the whole 30-second wake window, and no resume the platform would refuse.
    await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    await Assert.That(sandboxes.Resumes).IsEqualTo(0);
    await Assert.That(sandboxes.Gets).IsBetween(1, 2);
    await Assert.That(under.Renderer.Requests.Count).IsEqualTo(requests);
    var disabled = under.Logs.WithEventId(59);
    await Assert.That(disabled.Count).IsEqualTo(requests);
    await Assert.That(disabled[0]["TenantId"]).IsEqualTo("acme");
    await Assert.That(disabled[0]["SandboxId"]).IsEqualTo(SandboxId);
  }

  [Test]
  public async Task A_sandbox_enabled_again_is_resumed()
  {
    // After enable the data plane reports the stopped sandbox UserStopped.
    SleepingRenderer platform = new();
    FakeSandboxesClient sandboxes = new(
      _ => platform.State,
      (_, _) => platform.Wake(),
      _ => SandboxStoppedReasons.UserStopped
    );
    await using var under = await StartAsync(platform, sandboxes, SandboxesWake());

    using var response = await under.PostAsync();

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(sandboxes.Resumes).IsEqualTo(1);
    await Assert.That(under.Logs.WithEventId(59)).IsEmpty();
  }

  [Test]
  public async Task A_connection_that_stalls_while_the_renderer_wakes_is_retried()
  {
    SleepingRenderer platform = new();
    FakeSandboxesClient sandboxes = new(_ => platform.State, (_, _) => platform.Wake());
    var stalls = 0;
    await using var under = await GatewayUnderTest.StartAsync(
      platform.HandleAsync,
      SandboxesWake(),
      builder =>
      {
        builder.Services.AddSingleton<ISandboxesClient>(sandboxes);
        // What SocketsHttpHandler throws when the connection does not open within its connect
        // timeout: a cancellation the conversion's own token did not cause.
        AddRendererHandler(
          builder,
          () =>
            platform.State == SandboxStates.Running && Interlocked.Increment(ref stalls) == 1
              ? new TaskCanceledException("Connect timed out.", new TimeoutException())
              : null
        );
      },
      SandboxId
    );

    using var response = await under.PostAsync();

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(Volatile.Read(ref stalls)).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  [Arguments(false, SandboxId)]
  [Arguments(true, null)]
  public async Task Without_sandboxes_mode_and_a_sandbox_id_nothing_is_resumed(
    bool wake,
    string? sandboxId
  )
  {
    SleepingRenderer platform = new();
    FakeSandboxesClient sandboxes = new(_ => platform.State, (_, _) => platform.Wake());
    await using var under = await GatewayUnderTest.StartAsync(
      platform.HandleAsync,
      wake ? SandboxesWake() : [],
      builder => builder.Services.AddSingleton<ISandboxesClient>(sandboxes),
      sandboxId
    );

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer is not running.");
    await Assert.That(sandboxes.Gets).IsEqualTo(0);
    await Assert.That(sandboxes.Resumes).IsEqualTo(0);
  }

  [Test]
  public async Task The_renderers_own_403_never_wakes_anything()
  {
    SleepingRenderer platform = new();
    FakeSandboxesClient sandboxes = new(_ => platform.State, (_, _) => platform.Wake());
    await using var under = await GatewayUnderTest.StartAsync(
      context =>
        FakeRenderer.WriteProblemAsync(
          context,
          StatusCodes.Status403Forbidden,
          """{"kind":"Forbidden","error":"Sandbox is not running"}"""
        ),
      SandboxesWake(),
      builder => builder.Services.AddSingleton<ISandboxesClient>(sandboxes),
      SandboxId
    );

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer is unavailable.");
    await Assert.That(sandboxes.Gets).IsEqualTo(0);
    await Assert.That(sandboxes.Resumes).IsEqualTo(0);
  }

  [Test]
  public async Task Waking_carries_none_of_the_callers_trace_context()
  {
    ConcurrentQueue<Activity> requests = new();
    using ActivityListener listener = new()
    {
      ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
      ActivityStarted = requests.Enqueue,
    };
    ActivitySource.AddActivityListener(listener);
    SleepingRenderer platform = new();
    FakeSandboxesClient sandboxes = new(_ => platform.State, (_, _) => platform.Wake());
    await using var under = await StartAsync(platform, sandboxes, SandboxesWake());

    using HttpRequestMessage request = new(HttpMethod.Post, "/convert")
    {
      Content = new StringContent("""{"html":"<p>x</p>"}""", Encoding.UTF8, "application/json"),
    };
    request.Headers.Add("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    request.Headers.Add("baggage", "secret=caller-baggage");
    using var response = await under.Gateway.Client.SendAsync(request, TestToken);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(
        requests.Any(activity =>
          activity.TraceId.ToHexString() == "4bf92f3577b34da6a3ce929d0e0e4736"
        )
      )
      .IsTrue();
    // The state read and the resume ran under no activity of the caller's...
    await Assert.That(sandboxes.Activities.Count).IsEqualTo(2);
    await Assert.That(sandboxes.Activities.All(activity => activity is null)).IsTrue();
    // ...and the data-plane client would not send trace headers anyway.
    var factory = under.Gateway.Services.GetRequiredService<IHttpMessageHandlerFactory>();
    foreach (var name in new[] { SandboxesClientName, RendererClientName })
    {
      var handler = PrimaryHandler(factory.CreateHandler(name));
      await Assert.That(handler.ActivityHeadersPropagator).IsNull();
    }
  }

  private static SocketsHttpHandler PrimaryHandler(HttpMessageHandler handler)
  {
    while (handler is DelegatingHandler delegating)
    {
      handler = delegating.InnerHandler!;
    }

    return (SocketsHttpHandler)handler;
  }

  /// <summary>
  /// Puts a handler in front of the gateway's renderer client that throws what
  /// <paramref name="failure"/> returns, or lets the request through when it returns null.
  /// </summary>
  private static void AddRendererHandler(WebApplicationBuilder builder, Func<Exception?> failure) =>
    builder.Services.PostConfigure<HttpClientFactoryOptions>(
      RendererClientName,
      options =>
        options.HttpMessageHandlerBuilderActions.Add(handlers =>
          handlers.AdditionalHandlers.Add(new FailingHandler(failure))
        )
    );

  private static Task<GatewayUnderTest> StartAsync(
    SleepingRenderer platform,
    FakeSandboxesClient sandboxes,
    string[] settings
  ) =>
    GatewayUnderTest.StartAsync(
      platform.HandleAsync,
      settings,
      builder => builder.Services.AddSingleton<ISandboxesClient>(sandboxes),
      SandboxId
    );

  private sealed class FailingHandler(Func<Exception?> failure) : DelegatingHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken
    ) =>
      failure() is { } exception
        ? Task.FromException<HttpResponseMessage>(exception)
        : base.SendAsync(request, cancellationToken);
  }

  /// <summary>
  /// The Sandboxes proxy in front of a renderer: <c>403 {"error":"Sandbox is not running"}</c>
  /// while suspended, then a few <c>502</c>s while the route comes up, then the renderer's PDF.
  /// </summary>
  private sealed class SleepingRenderer
  {
    private int _asleep = 1;
    private int _startingAnswers;
    private int _notRunningAnswers;

    public int NotRunningAnswers => Volatile.Read(ref _notRunningAnswers);

    /// <summary>The sandbox's state, as the data plane reports it.</summary>
    public string State =>
      Volatile.Read(ref _asleep) == 1 ? SandboxStates.Stopped : SandboxStates.Running;

    public Task Wake(int startingAnswers = 0)
    {
      Volatile.Write(ref _startingAnswers, startingAnswers);
      Volatile.Write(ref _asleep, 0);
      return Task.CompletedTask;
    }

    public async Task HandleAsync(HttpContext context)
    {
      if (Volatile.Read(ref _asleep) == 1)
      {
        Interlocked.Increment(ref _notRunningAnswers);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
          """{"error":"Sandbox is not running"}""",
          context.RequestAborted
        );
        return;
      }

      if (Interlocked.Decrement(ref _startingAnswers) >= 0)
      {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("upstream connect error", context.RequestAborted);
        return;
      }

      await FakeRenderer.WritePdfAsync(context, "%PDF-1.7 awake");
    }
  }
}
