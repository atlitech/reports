using System.Diagnostics;
using System.Net;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Hosting.Sandboxes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Atli.Reports.Engine.Tests.Server.Gateway.GatewayHost;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>
/// Waking suspended Azure Container Apps sandboxes: the platform's proxy answers
/// <c>403 {"error":"Sandbox is not running"}</c> until the gateway resumes the sandbox through the
/// Sandboxes data plane, and the gateway then sends the conversion again.
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
    FakeSandboxesClient sandboxes = new((_, _) => platform.Wake(startingAnswers: 1));
    await using var under = await StartAsync(platform, sandboxes, SandboxesWake());

    using var response = await under.PostAsync();

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(await response.Content.ReadAsStringAsync(TestToken))
      .IsEqualTo("%PDF-1.7 awake");
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
  public async Task Concurrent_requests_share_one_resume()
  {
    const int requests = 5;
    SleepingRenderer platform = new();
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    FakeSandboxesClient sandboxes = new(
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

    await Assert.That(sandboxes.Resumes).IsEqualTo(1);
  }

  [Test]
  public async Task A_failed_resume_is_tried_again_within_the_wake_window()
  {
    SleepingRenderer platform = new();
    var attempts = 0;
    FakeSandboxesClient sandboxes = new(
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
    await Assert.That(under.Logs.WithEventId(62)).HasSingleItem();
  }

  [Test]
  public async Task A_renderer_that_does_not_wake_in_time_is_unavailable()
  {
    SleepingRenderer platform = new();
    FakeSandboxesClient sandboxes = new((_, _) => Task.CompletedTask);
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
  [Arguments(false, SandboxId)]
  [Arguments(true, null)]
  public async Task Without_sandboxes_mode_and_a_sandbox_id_nothing_is_resumed(
    bool wake,
    string? sandboxId
  )
  {
    SleepingRenderer platform = new();
    FakeSandboxesClient sandboxes = new((_, _) => platform.Wake());
    await using var under = await GatewayUnderTest.StartAsync(
      platform.HandleAsync,
      wake ? SandboxesWake() : [],
      builder => builder.Services.AddSingleton<ISandboxesClient>(sandboxes),
      sandboxId
    );

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer is not running.");
    await Assert.That(sandboxes.Resumes).IsEqualTo(0);
  }

  [Test]
  public async Task The_renderers_own_403_never_wakes_anything()
  {
    SleepingRenderer platform = new();
    FakeSandboxesClient sandboxes = new((_, _) => platform.Wake());
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
    await Assert.That(sandboxes.Resumes).IsEqualTo(0);
  }

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
