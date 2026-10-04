using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Atli.Reports.Engine.Tests.Support;
using Microsoft.AspNetCore.Http;
using static Atli.Reports.Engine.Tests.Server.Gateway.GatewayHost;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>
/// The gateway treats every renderer answer as untrusted: a <c>200</c> must be a PDF before the
/// caller's response starts, the PDF is capped and must arrive whole, the renderer gets a deadline,
/// and its errors pass on only as far as they are safe.
/// </summary>
public class GatewayResponseTests
{
  private const string ProblemJson = "application/problem+json";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task A_pdf_streams_through_with_the_endpoints_headers()
  {
    var pdf = "%PDF-1.7 " + new string('x', 300_000) + " %%EOF";
    await using var under = await GatewayUnderTest.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, pdf)
    );

    using var response = await under.PostAsync();

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/pdf");
    await Assert
      .That(response.Content.Headers.ContentDisposition?.DispositionType)
      .IsEqualTo("attachment");
    await Assert.That(await response.Content.ReadAsStringAsync(TestToken)).IsEqualTo(pdf);
  }

  [Test]
  [Arguments("text/html", "%PDF-1.7 with the wrong type")]
  [Arguments("application/octet-stream", "%PDF-1.7 with the wrong type")]
  [Arguments("application/pdf", "<html>not a pdf</html>")]
  [Arguments("application/pdf", "%PD")]
  [Arguments("application/pdf", "")]
  public async Task A_200_that_is_not_a_pdf_fails_before_the_response_starts(
    string contentType,
    string body
  )
  {
    await using var under = await GatewayUnderTest.StartAsync(async context =>
    {
      context.Response.ContentType = contentType;
      await context.Response.WriteAsync(body, context.RequestAborted);
    });

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Status).IsEqualTo(500);
    await Assert.That(problem.Kind).IsEqualTo("RenderFailed");
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer did not answer with a PDF.");
    await Assert.That(under.Logs.WithEventId(46)).HasSingleItem();
  }

  [Test]
  public async Task A_200_without_length_framing_is_not_trusted()
  {
    // Ends with the connection, so a body cut short would look complete.
    await using var renderer = new RawHttpResponder(
      "HTTP/1.1 200 OK\r\nContent-Type: application/pdf\r\nConnection: close\r\n\r\n%PDF-1.7 unframed"
    );
    await using var gateway = await GatewayHost.StartAsync(OneTenant(renderer.BaseUrl));

    var problem = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));

    await Assert.That(problem.Status).IsEqualTo(500);
    await Assert.That(problem.Kind).IsEqualTo("RenderFailed");
  }

  [Test]
  public async Task A_pdf_declared_over_the_limit_fails_before_the_response_starts()
  {
    await using var under = await GatewayUnderTest.StartAsync(
      async context =>
      {
        context.Response.ContentLength = 4096;
        await FakeRenderer.WritePdfAsync(context, "%PDF-" + new string('x', 4091));
      },
      ["--ReportsServer:Gateway:MaxPdfBytes=1024"]
    );

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Status).IsEqualTo(500);
    await Assert.That(problem.Kind).IsEqualTo("RenderFailed");
    await Assert.That(under.Logs.WithEventId(47)).HasSingleItem();
  }

  [Test]
  [Arguments("over the size limit")]
  [Arguments("reset with a chunked body")]
  [Arguments("reset with a content length")]
  [Arguments("hung past the renderer deadline")]
  public async Task A_pdf_that_fails_after_it_started_aborts_the_response(string failure)
  {
    TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var under = await GatewayUnderTest.StartAsync(
      async context =>
      {
        context.Response.ContentType = "application/pdf";
        if (failure == "reset with a content length")
        {
          context.Response.ContentLength = 1000;
        }

        await context.Response.WriteAsync(
          "%PDF-1.7 " + new string('x', 200),
          context.RequestAborted
        );
        await context.Response.Body.FlushAsync(context.RequestAborted);
        // The rest only once the caller has its 200: the failure must come after the start.
        await started.Task.WaitAsync(context.RequestAborted);
        switch (failure)
        {
          case "over the size limit":
            await context.Response.WriteAsync(new string('x', 64 * 1024), context.RequestAborted);
            break;
          case "hung past the renderer deadline":
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
            break;
          default:
            context.Abort();
            break;
        }
      },
      [
        "--ReportsServer:Gateway:MaxPdfBytes=1024",
        // Only the hung renderer needs a short deadline; it must still let the first chunk
        // through on a busy test machine.
        $"--ReportsServer:Gateway:RendererTimeout={(failure == "hung past the renderer deadline" ? "00:00:05" : "00:01:30")}",
      ]
    );

    using var response = await under.PostForHeadersAsync(TestToken);
    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    started.SetResult();

    // A complete 200 must never arrive: the body breaks off.
    await Assert
      .That(async () => await response.Content.ReadAsByteArrayAsync(TestToken))
      .Throws<HttpRequestException>();
  }

  [Test]
  public async Task A_renderer_past_its_deadline_is_a_timeout_and_its_request_is_canceled()
  {
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var under = await GatewayUnderTest.StartAsync(
      async context =>
      {
        entered.TrySetResult();
        try
        {
          await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
          aborted.TrySetResult();
        }
      },
      ["--ReportsServer:Gateway:RendererTimeout=00:00:02"]
    );

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Status).IsEqualTo(504);
    await Assert.That(problem.Kind).IsEqualTo("Timeout");
    await Assert.That(under.Logs.WithEventId(49)).HasSingleItem();
    // On a loaded machine the deadline can pass before the request reaches the handler; when it
    // did reach it, the gateway must have canceled it.
    if (entered.Task.IsCompleted)
    {
      await aborted.Task.WaitAsync(TestEngine.GenerousTimeout, TestToken);
    }
  }

  [Test]
  public async Task A_caller_that_goes_away_cancels_the_renderer_request()
  {
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var under = await GatewayUnderTest.StartAsync(async context =>
    {
      entered.TrySetResult();
      try
      {
        await Task.Delay(Timeout.Infinite, context.RequestAborted);
      }
      catch (OperationCanceledException)
      {
        aborted.TrySetResult();
      }
    });

    using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(
      TestToken
    );
    var send = under.PostForHeadersAsync(caller.Token);
    await entered.Task.WaitAsync(TestToken);
    await caller.CancelAsync();

    await Assert.That(async () => await send).Throws<OperationCanceledException>();
    await aborted.Task.WaitAsync(TestEngine.GenerousTimeout, TestToken);
  }

  [Test]
  [Arguments(
    400,
    ProblemJson,
    """{"kind":"InvalidRequest","detail":"Unknown orientation 'sideways'."}""",
    400,
    "InvalidRequest",
    "Unknown orientation 'sideways'.",
    0
  )]
  [Arguments(
    422,
    ProblemJson,
    """{"kind":"SignalTimeout","detail":"The page never called pdfReady."}""",
    422,
    "SignalTimeout",
    "The page never called pdfReady.",
    0
  )]
  [Arguments(
    422,
    ProblemJson,
    """{"kind":"PolicyDenied","detail":"The document fetched an external asset."}""",
    422,
    "PolicyDenied",
    "The document fetched an external asset.",
    0
  )]
  [Arguments(
    400,
    ProblemJson,
    """{"kind":"InvalidRequest"}""",
    400,
    "InvalidRequest",
    "The tenant's renderer rejected the request.",
    0
  )]
  [Arguments(
    500,
    ProblemJson,
    """{"kind":"RenderFailed","detail":"Chromium crashed in /opt/renderer/internal"}""",
    500,
    "RenderFailed",
    "The tenant's renderer could not convert the document.",
    0
  )]
  [Arguments(
    503,
    ProblemJson,
    """{"kind":"Busy","detail":"The queue is full."}""",
    503,
    "Busy",
    "The tenant's renderer is busy. Retry later.",
    1
  )]
  [Arguments(
    429,
    ProblemJson,
    """{"kind":"Busy"}""",
    503,
    "Busy",
    "The tenant's renderer is busy. Retry later.",
    1
  )]
  [Arguments(
    503,
    ProblemJson,
    """{"kind":"BrowserUnavailable","detail":"The browser is restarting."}""",
    503,
    "BrowserUnavailable",
    "The tenant's renderer is unavailable.",
    5
  )]
  [Arguments(
    504,
    ProblemJson,
    """{"kind":"Timeout","detail":"A browser command timed out."}""",
    504,
    "Timeout",
    "The tenant's renderer did not finish the conversion in time.",
    0
  )]
  [Arguments(
    401,
    ProblemJson,
    """{"kind":"Unauthorized"}""",
    503,
    "BrowserUnavailable",
    "The tenant's renderer is unavailable.",
    5
  )]
  [Arguments(
    403,
    ProblemJson,
    """{"kind":"Forbidden"}""",
    503,
    "BrowserUnavailable",
    "The tenant's renderer is unavailable.",
    5
  )]
  [Arguments(
    400,
    ProblemJson,
    """{"kind":"Canceled","detail":"Never from a server."}""",
    500,
    "RenderFailed",
    "The tenant's renderer could not convert the document.",
    0
  )]
  [Arguments(
    400,
    ProblemJson,
    """{"kind":"Bogus","detail":"Unknown kind."}""",
    500,
    "RenderFailed",
    "The tenant's renderer could not convert the document.",
    0
  )]
  [Arguments(
    400,
    ProblemJson,
    """{"kind":"1","detail":"A numeric kind."}""",
    500,
    "RenderFailed",
    "The tenant's renderer could not convert the document.",
    0
  )]
  [Arguments(
    400,
    ProblemJson,
    "not json",
    500,
    "RenderFailed",
    "The tenant's renderer could not convert the document.",
    0
  )]
  [Arguments(
    502,
    "text/plain",
    "upstream connect error",
    503,
    "BrowserUnavailable",
    "The tenant's renderer is unavailable.",
    5
  )]
  [Arguments(
    404,
    "text/html",
    "<h1>Not found</h1>",
    500,
    "RenderFailed",
    "The tenant's renderer could not convert the document.",
    0
  )]
  [Arguments(
    403,
    "application/json",
    """{"error":"Sandbox is not running"}""",
    503,
    "BrowserUnavailable",
    "The tenant's renderer is not running.",
    5
  )]
  public async Task Renderer_errors_become_the_callers_problem(
    int rendererStatus,
    string contentType,
    string body,
    int status,
    string kind,
    string detail,
    int retryAfterSeconds
  )
  {
    await using var under = await GatewayUnderTest.StartAsync(async context =>
    {
      context.Response.StatusCode = rendererStatus;
      context.Response.ContentType = contentType;
      // A renderer's own Retry-After never reaches the caller; the kind decides it.
      context.Response.Headers.RetryAfter = "3600";
      await context.Response.WriteAsync(body, context.RequestAborted);
    });

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Status).IsEqualTo(status);
    await Assert.That(problem.Kind).IsEqualTo(kind);
    await Assert.That(problem.Detail).IsEqualTo(detail);
    await Assert
      .That(problem.RetryAfter)
      .IsEqualTo(retryAfterSeconds == 0 ? null : TimeSpan.FromSeconds(retryAfterSeconds));
  }

  [Test]
  public async Task A_renderer_detail_is_stripped_of_control_characters_and_cut_short()
  {
    var detail = "  Bad\u0000 option\u001b[31m‮evil\r\n" + new string('x', 1000) + "\U0001F600";
    await using var under = await GatewayUnderTest.StartAsync(context =>
      FakeRenderer.WriteProblemAsync(
        context,
        400,
        JsonSerializer.Serialize(
          new Dictionary<string, string> { ["kind"] = "InvalidRequest", ["detail"] = detail }
        )
      )
    );

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Kind).IsEqualTo("InvalidRequest");
    await Assert.That(problem.Detail!.Length).IsEqualTo(512);
    await Assert.That(problem.Detail).StartsWith("Bad option[31mevil" + "xxx");
    await Assert.That(problem.Detail.Any(c => char.IsControl(c) || c == '‮')).IsFalse();
  }

  [Test]
  public async Task An_error_body_over_the_read_limit_is_not_believed()
  {
    var detail = new string('x', 20_000);
    await using var under = await GatewayUnderTest.StartAsync(context =>
      FakeRenderer.WriteProblemAsync(
        context,
        400,
        $$"""{"kind":"InvalidRequest","detail":"{{detail}}"}"""
      )
    );

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Status).IsEqualTo(500);
    await Assert.That(problem.Kind).IsEqualTo("RenderFailed");
    await Assert.That(problem.Detail).DoesNotContain("xxxx");
  }

  [Test]
  public async Task An_unreachable_renderer_is_unavailable()
  {
    int port;
    using (TcpListener probe = new(IPAddress.Loopback, 0))
    {
      probe.Start();
      port = ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    await using var gateway = await GatewayHost.StartAsync(OneTenant($"http://127.0.0.1:{port}"));

    var problem = await ReadProblemAsync(await gateway.PostAsync("""{"html":"<p>x</p>"}"""));

    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(problem.Detail).IsEqualTo("The tenant's renderer could not be reached.");
  }

  [Test]
  public async Task A_redirect_is_not_followed()
  {
    await using var elsewhere = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7 elsewhere")
    );
    await using var under = await GatewayUnderTest.StartAsync(context =>
    {
      context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
      context.Response.Headers.Location = elsewhere.BaseUrl + "/convert";
      return Task.CompletedTask;
    });

    var problem = await ReadProblemAsync(await under.PostAsync());

    await Assert.That(problem.Status).IsEqualTo(500);
    await Assert.That(elsewhere.Requests).IsEmpty();
  }
}
