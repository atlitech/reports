using System.Net;
using System.Text.Json;
using Atli.Reports.Engine.Tests.Support;
using static Atli.Reports.Engine.Tests.Server.SecurityAuthenticationTests;

namespace Atli.Reports.Engine.Tests.Server;

/// <summary>Admission spans uploads, engine work, and response streaming; leases always release.</summary>
public class SecurityAdmissionTests
{
  private const string Credential = "primary.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Caller_quota_cannot_be_bypassed_by_rotated_keys_or_tenant_headers()
  {
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var converter = new FakeConverter(
      async (_, cancellation) =>
      {
        entered.TrySetResult();
        await release.Task.WaitAsync(cancellation);
        return null;
      }
    );
    const string rotated = "rotated.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
    await using var server = await SecurityServer.StartAsync(
      converter,
      [
        .. ApiKeySettings(),
        .. KeySettings(1, "rotated", rotated),
        "--ReportsServer:Limits:MaxConcurrentRequestsPerCaller=1",
        "--ReportsServer:Callers:0:CallerId=billing",
        "--ReportsServer:Callers:0:Limits:MaxRequestBodyBytes=1024",
      ]
    );
    using var firstRequest = ConvertRequest(credential: Credential);
    var first = server.Client.SendAsync(firstRequest, TestToken);
    await entered.Task.WaitAsync(TestToken);
    try
    {
      using var secondRequest = ConvertRequest(credential: rotated);
      secondRequest.Headers.Add("X-Tenant-Id", "another-tenant");
      secondRequest.Headers.Add("X-Reports-Caller-Id", "another-caller");
      using var busy = await server.Client.SendAsync(secondRequest, TestToken);
      await Assert.That(busy.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
      await Assert.That(busy.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
      using var problem = JsonDocument.Parse(await busy.Content.ReadAsStringAsync(TestToken));
      await Assert.That(problem.RootElement.GetProperty("kind").GetString()).IsEqualTo("Busy");
      await Assert.That(converter.Calls).IsEqualTo(1);
    }
    finally
    {
      release.TrySetResult();
    }
    using var completed = await first;
    using var nextRequest = ConvertRequest(credential: Credential);
    using var next = await server.Client.SendAsync(nextRequest, TestToken);
    await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.OK);
  }

  [Test]
  public async Task Different_callers_have_separate_quotas_and_share_a_bounded_global_limit()
  {
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var converter = new FakeConverter(
      async (_, cancellation) =>
      {
        entered.TrySetResult();
        await release.Task.WaitAsync(cancellation);
        return null;
      }
    );
    var secondKey = "other." + new string('x', 43);
    await using var server = await SecurityServer.StartAsync(
      converter,
      [
        .. ApiKeySettings(),
        .. KeySettings(1, "other", secondKey, caller: "other"),
        "--ReportsServer:MaxConcurrentRequests=1",
        "--ReportsServer:Limits:MaxConcurrentRequestsPerCaller=1",
      ]
    );
    using var firstRequest = ConvertRequest(credential: Credential);
    var first = server.Client.SendAsync(firstRequest, TestToken);
    await entered.Task.WaitAsync(TestToken);
    try
    {
      using var secondRequest = ConvertRequest(credential: secondKey);
      using var busy = await server.Client.SendAsync(secondRequest, TestToken);
      await Assert.That(busy.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
      await Assert.That(busy.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
    }
    finally
    {
      release.TrySetResult();
    }
    using var completed = await first;
    using var nextRequest = ConvertRequest(credential: secondKey);
    using var next = await server.Client.SendAsync(nextRequest, TestToken);
    await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.OK);
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task Invalid_requests_release_admission_and_caller_policy_sets_body_limit(
    bool chunkedContent
  )
  {
    var converter = SuccessfulConverter();
    await using var server = await SecurityServer.StartAsync(
      converter,
      [
        .. ApiKeySettings(),
        "--ReportsServer:Limits:MaxConcurrentRequestsPerCaller=1",
        "--ReportsServer:Callers:0:CallerId=billing",
        "--ReportsServer:Callers:0:Limits:MaxRequestBodyBytes=64",
      ]
    );
    using (var request = ConvertRequest("invalid", Credential))
    using (var response = await server.Client.SendAsync(request, TestToken))
    {
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
    using (var request = ConvertRequest($$"""{"html":"{{new string('x', 65)}}"}""", Credential))
    {
      request.Headers.Add("X-Reports-Caller-Id", "unlimited");
      request.Headers.TransferEncodingChunked = chunkedContent;
      using var response = await server.Client.SendAsync(request, TestToken);
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
    }
    using (var request = ConvertRequest(credential: Credential))
    using (var response = await server.Client.SendAsync(request, TestToken))
    {
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
    await Assert.That(converter.Calls).IsEqualTo(1);
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task Deadline_cancels_work_returns_504_and_releases_quota(bool returnCanceledError)
  {
    var calls = 0;
    var converter = new FakeConverter(
      async (_, cancellation) =>
      {
        if (Interlocked.Increment(ref calls) == 1)
        {
          try
          {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
          }
          catch (OperationCanceledException) when (returnCanceledError)
          {
            return new ConversionError(ConversionErrorKind.Canceled, "The client left.");
          }
        }
        return null;
      }
    );
    await using var server = await SecurityServer.StartAsync(
      converter,
      [
        .. ApiKeySettings(),
        "--ReportsServer:Limits:MaxConcurrentRequestsPerCaller=1",
        "--ReportsServer:Limits:RequestTimeout=00:00:00.100",
      ]
    );
    using (var request = ConvertRequest(credential: Credential))
    using (var response = await server.Client.SendAsync(request, TestToken))
    {
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.GatewayTimeout);
      using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestToken));
      await Assert.That(problem.RootElement.GetProperty("kind").GetString()).IsEqualTo("Timeout");
    }
    using (var request = ConvertRequest(credential: Credential))
    using (var response = await server.Client.SendAsync(request, TestToken))
    {
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
  }

  [Test]
  public async Task Client_disconnect_cancels_work_and_releases_quota()
  {
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var converter = new FakeConverter(
      async (_, cancellation) =>
      {
        if (Interlocked.Increment(ref calls) == 1)
        {
          entered.TrySetResult();
          await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        }
        return null;
      }
    );
    await using var server = await SecurityServer.StartAsync(
      converter,
      [.. ApiKeySettings(), "--ReportsServer:Limits:MaxConcurrentRequestsPerCaller=1"]
    );
    using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
    using var firstRequest = ConvertRequest(credential: Credential);
    var first = server.Client.SendAsync(firstRequest, canceled.Token);
    await entered.Task.WaitAsync(TestToken);
    await canceled.CancelAsync();
    await Assert.That(async () => await first).Throws<OperationCanceledException>();
    await Assert
      .That(
        await TestEngine.EventuallyAsync(
          async () =>
          {
            using var request = ConvertRequest(credential: Credential);
            using var response = await server.Client.SendAsync(request, TestToken);
            return response.StatusCode == HttpStatusCode.OK;
          },
          TimeSpan.FromSeconds(5)
        )
      )
      .IsTrue();
  }

  [Test]
  public async Task Deadline_after_streaming_starts_aborts_the_pdf_response()
  {
    var converter = new FakeConverter(
      async (destination, cancellation) =>
      {
        await destination.WriteAsync("%PDF-"u8.ToArray(), cancellation);
        await destination.FlushAsync(cancellation);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        return null;
      }
    );
    await using var server = await SecurityServer.StartAsync(
      converter,
      [.. ApiKeySettings(), "--ReportsServer:Limits:RequestTimeout=00:00:00.100"]
    );
    using var request = ConvertRequest(credential: Credential);
    await Assert
      .That(async () => await server.Client.SendAsync(request, TestToken))
      .Throws<HttpRequestException>();
  }
}
