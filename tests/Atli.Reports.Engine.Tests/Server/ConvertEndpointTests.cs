using System.Net;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Server.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine.Tests.Server;

/// <summary>
/// Runs the reports server on a free loopback port. Most tests replace the converter with a fake, so
/// they check the HTTP contract without a browser.
/// </summary>
public class ConvertEndpointTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [Arguments(ConversionErrorKind.InvalidRequest, 400, null)]
  [Arguments(ConversionErrorKind.SignalTimeout, 422, null)]
  [Arguments(ConversionErrorKind.Busy, 503, "1")]
  [Arguments(ConversionErrorKind.BrowserUnavailable, 503, "5")]
  [Arguments(ConversionErrorKind.Timeout, 504, null)]
  [Arguments(ConversionErrorKind.RenderFailed, 500, null)]
  public async Task Conversion_errors_map_to_problem_details_with_their_status(
    ConversionErrorKind kind,
    int status,
    string? retryAfter
  )
  {
    await using var server = await RunningServer.StartAsync(
      new FakeConverter(
        (_, _) => Task.FromResult<ConversionError?>(new ConversionError(kind, "It failed."))
      )
    );

    using var response = await server.PostAsync("""{"html":"<p>x</p>"}""");

    await Assert.That((int)response.StatusCode).IsEqualTo(status);
    await Assert
      .That(response.Content.Headers.ContentType?.MediaType)
      .IsEqualTo("application/problem+json");
    await Assert
      .That(
        response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(
          System.Globalization.CultureInfo.InvariantCulture
        )
      )
      .IsEqualTo(retryAfter);
    using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestToken));
    await Assert.That(problem.RootElement.GetProperty("status").GetInt32()).IsEqualTo(status);
    await Assert
      .That(problem.RootElement.GetProperty("kind").GetString())
      .IsEqualTo(kind.ToString());
    await Assert
      .That(problem.RootElement.GetProperty("detail").GetString())
      .IsEqualTo("It failed.");
  }

  [Test]
  public async Task Every_error_kind_has_a_status_and_cancellation_is_499()
  {
    foreach (var kind in Enum.GetValues<ConversionErrorKind>())
    {
      await Assert.That(ConversionProblems.StatusCode(kind)).IsBetween(400, 599);
    }

    await Assert.That(ConversionProblems.StatusCode(ConversionErrorKind.Canceled)).IsEqualTo(499);
  }

  [Test]
  public async Task A_converted_pdf_streams_back_as_an_attachment()
  {
    await using var server = await RunningServer.StartAsync(
      new FakeConverter(
        async (destination, cancellationToken) =>
        {
          await destination.WriteAsync("%PDF-1.7 "u8.ToArray(), cancellationToken);
          await destination.WriteAsync("body %%EOF"u8.ToArray(), cancellationToken);
          return null;
        }
      )
    );

    using var response = await server.PostAsync("""{"html":"<p>x</p>"}""");

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/pdf");
    await Assert
      .That(response.Content.Headers.ContentDisposition?.DispositionType)
      .IsEqualTo("attachment");
    await Assert
      .That(response.Content.Headers.ContentDisposition?.FileName)
      .IsEqualTo("output.pdf");
    await Assert
      .That(await response.Content.ReadAsStringAsync(TestToken))
      .IsEqualTo("%PDF-1.7 body %%EOF");
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task A_failure_after_the_first_byte_aborts_the_response(bool throws)
  {
    await using var server = await RunningServer.StartAsync(
      new FakeConverter(
        async (destination, cancellationToken) =>
        {
          await destination.WriteAsync(new byte[64 * 1024], cancellationToken);
          await destination.FlushAsync(cancellationToken);
          // An unhandled exception must not become a problem response either: the PDF already started.
          return throws
            ? throw new InvalidOperationException("The converter broke.")
            : new ConversionError(ConversionErrorKind.BrowserUnavailable, "The browser died.");
        }
      )
    );

    using HttpRequestMessage request = new(HttpMethod.Post, "/convert")
    {
      Content = new StringContent("""{"html":"<p>x</p>"}""", Encoding.UTF8, "application/json"),
    };

    // Either the headers never arrive or the body breaks off; a complete 200 must not.
    var completed = await Assert
      .That(async () =>
      {
        using var response = await server.Client.SendAsync(
          request,
          HttpCompletionOption.ResponseHeadersRead,
          TestToken
        );
        await response.Content.ReadAsByteArrayAsync(TestToken);
      })
      .Throws<HttpRequestException>();
    await Assert.That(completed).IsNotNull();
  }

  [Test]
  public async Task Blank_html_is_a_bad_request_and_never_reaches_the_converter()
  {
    FakeConverter converter = new((_, _) => throw new InvalidOperationException("unreachable"));
    await using var server = await RunningServer.StartAsync(converter);

    using var response = await server.PostAsync("""{"html":"   "}""");

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert.That(converter.Calls).IsEqualTo(0);
  }

  [Test]
  [Arguments("not json")]
  [Arguments("""{"html":"<p>x</p>" """)]
  [Arguments("")]
  [Arguments("null")]
  [Arguments("[]")]
  [Arguments("""{"options":{"paperSize":"a4"}}""")]
  [Arguments("""{"html":5}""")]
  [Arguments("""{"html":"<p>x</p>","options":{"scale":"large"}}""")]
  [Arguments("""{"html":"<p>a</p>","html":"<p>b</p>"}""")]
  [Arguments("""{"html":"<p>a</p>","Html":"<p>b</p>"}""")]
  [Arguments("""{"html":"<p>x</p>","options":{"scale":1,"scale":2}}""")]
  public async Task Bodies_that_are_not_conversion_requests_are_bad_requests_with_problem_details(
    string body
  )
  {
    FakeConverter converter = new((_, _) => throw new InvalidOperationException("unreachable"));
    await using var server = await RunningServer.StartAsync(converter);

    using var response = await server.PostAsync(body);

    var detail = await AssertProblemAsync(response, 400, ConversionErrorKind.InvalidRequest);
    await Assert.That(detail).StartsWith("The request body is not a conversion request.");
    await Assert.That(converter.Calls).IsEqualTo(0);
  }

  [Test]
  [Arguments("text/plain")]
  [Arguments(null)]
  public async Task Bodies_that_are_not_json_are_unsupported_media_types_with_problem_details(
    string? contentType
  )
  {
    FakeConverter converter = new((_, _) => throw new InvalidOperationException("unreachable"));
    await using var server = await RunningServer.StartAsync(converter);

    using var response = await server.Client.PostAsync(
      "/convert",
      Content("""{"html":"<p>x</p>"}""", contentType),
      TestToken
    );

    var detail = await AssertProblemAsync(response, 415, ConversionErrorKind.InvalidRequest);
    await Assert.That(detail).Contains("Content-Type: application/json");
    await Assert.That(converter.Calls).IsEqualTo(0);
  }

  [Test]
  [Arguments("application/json", "not json", 400)]
  [Arguments("text/plain", """{"html":"<p>x</p>"}""", 415)]
  public async Task In_development_unreadable_requests_are_still_client_errors(
    string contentType,
    string body,
    int status
  )
  {
    // Development makes request binding throw its errors rather than set them.
    FakeConverter converter = new((_, _) => throw new InvalidOperationException("unreachable"));
    await using var server = await RunningServer.StartAsync(converter, "--environment=Development");

    using var response = await server.Client.PostAsync(
      "/convert",
      Content(body, contentType),
      TestToken
    );

    await AssertProblemAsync(response, status, ConversionErrorKind.InvalidRequest);
    await Assert.That(converter.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task A_body_over_the_configured_limit_is_too_large_with_problem_details()
  {
    FakeConverter converter = new((_, _) => Task.FromResult<ConversionError?>(null));
    // The command line feeds the same configuration as Kestrel__Limits__MaxRequestBodySize.
    await using var server = await RunningServer.StartAsync(
      converter,
      "--Kestrel:Limits:MaxRequestBodySize=64"
    );

    using var small = await server.PostAsync("""{"html":"<p>x</p>"}""");
    using var large = await server.PostAsync($$"""{"html":"<p>{{new string('x', 64)}}</p>"}""");

    await Assert.That(small.StatusCode).IsEqualTo(HttpStatusCode.OK);
    var detail = await AssertProblemAsync(large, 413, ConversionErrorKind.InvalidRequest);
    await Assert.That(detail).IsEqualTo("The request body is larger than the server accepts.");
    await Assert.That(converter.Calls).IsEqualTo(1);
  }

  [Test]
  public async Task An_unhandled_exception_before_the_first_byte_is_a_server_error_with_problem_details()
  {
    await using var server = await RunningServer.StartAsync(
      new FakeConverter((_, _) => throw new InvalidOperationException("Internal state."))
    );

    using var response = await server.PostAsync("""{"html":"<p>x</p>"}""");

    await AssertProblemAsync(response, 500, ConversionErrorKind.RenderFailed);
    await Assert
      .That(await response.Content.ReadAsStringAsync(TestToken))
      .DoesNotContain("Internal state.")
      .Because("exception details stay in the server's logs");
  }

  [Test]
  public async Task A_canceled_conversion_answers_499_without_a_body()
  {
    await using var server = await RunningServer.StartAsync(
      new FakeConverter(
        (_, _) =>
          Task.FromResult<ConversionError?>(
            new ConversionError(ConversionErrorKind.Canceled, "The client left.")
          )
      )
    );

    using var response = await server.PostAsync("""{"html":"<p>x</p>"}""");

    await Assert.That((int)response.StatusCode).IsEqualTo(499);
    await Assert.That(response.Content.Headers.ContentType).IsNull();
    await Assert.That(await response.Content.ReadAsStringAsync(TestToken)).IsEmpty();
  }

  [Test]
  [Arguments("GET", "/convert", 405)]
  [Arguments("POST", "/nowhere", 404)]
  public async Task Requests_for_no_endpoint_get_problem_details_too(
    string method,
    string path,
    int status
  )
  {
    await using var server = await RunningServer.StartAsync(
      new FakeConverter((_, _) => throw new InvalidOperationException("unreachable"))
    );

    using HttpRequestMessage request = new(new HttpMethod(method), path);
    using var response = await server.Client.SendAsync(request, TestToken);

    await AssertProblemAsync(response, status, ConversionErrorKind.InvalidRequest);
  }

  [Test]
  public async Task Request_options_reach_the_converter()
  {
    PdfOptions? received = null;
    await using var server = await RunningServer.StartAsync(
      new FakeConverter(
        (_, _) => Task.FromResult<ConversionError?>(null),
        options => received = options
      )
    );

    using var response = await server.PostAsync(
      """
      {"html":"<p>x</p>","options":{"orientation":"landscape","paperSize":"a4","waitForSignal":"ready","waitTimeoutSeconds":5,"pageRanges":"1-2"}}
      """
    );

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(received!.Orientation).IsEqualTo(PageOrientation.Landscape);
    await Assert.That(received.PaperSize).IsEqualTo(PaperSize.A4);
    await Assert.That(received.WaitForSignal).IsEqualTo("ready");
    await Assert.That(received.WaitTimeout).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(received.PageRanges).IsEqualTo("1-2");
  }

  [Test]
  public async Task Custom_paper_sizes_tagging_and_an_infinite_signal_wait_reach_the_converter()
  {
    PdfOptions? received = null;
    await using var server = await RunningServer.StartAsync(
      new FakeConverter(
        (_, _) => Task.FromResult<ConversionError?>(null),
        options => received = options
      )
    );

    using var response = await server.PostAsync(
      """
      {"html":"<p>x</p>","options":{"orientation":"LANDSCAPE","paperSize":"a4","paperWidth":5.5,"paperHeight":7.25,"generateTaggedPdf":false,"waitForSignal":"ready","waitTimeoutSeconds":-0.001}}
      """
    );

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(received!.Orientation).IsEqualTo(PageOrientation.Landscape);
    await Assert.That(received.PaperSize).IsEqualTo(new PaperSize { Width = 5.5, Height = 7.25 });
    await Assert.That(received.GenerateTaggedPdf).IsFalse();
    await Assert.That(received.WaitTimeout).IsEqualTo(Timeout.InfiniteTimeSpan);
  }

  [Test]
  [Arguments("""{"orientation":"Portrait","paperSize":"Legal"}""")]
  [Arguments("""{"orientation":"Portrait","paperSize":"Legal","generateTaggedPdf":null}""")]
  public async Task Omitted_or_null_tagging_uses_the_explicit_tagged_default(string options)
  {
    PdfOptions? received = null;
    await using var server = await RunningServer.StartAsync(
      new FakeConverter(
        (_, _) => Task.FromResult<ConversionError?>(null),
        options => received = options
      )
    );

    using var response = await server.PostAsync($$"""{"html":"<p>x</p>","options":{{options}}}""");

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(received!.Orientation).IsEqualTo(PageOrientation.Portrait);
    await Assert.That(received.PaperSize).IsEqualTo(PaperSize.Legal);
    await Assert.That(received.GenerateTaggedPdf).IsTrue();
  }

  [Test]
  [Arguments("""{"orientation":"sideways"}""")]
  [Arguments("""{"orientation":""}""")]
  [Arguments("""{"paperSize":"tabloid"}""")]
  [Arguments("""{"paperWidth":5}""")]
  [Arguments("""{"paperHeight":7}""")]
  [Arguments("""{"paperWidth":0,"paperHeight":7}""")]
  [Arguments("""{"paperWidth":5,"paperHeight":-1}""")]
  public async Task Invalid_orientations_and_paper_sizes_are_bad_requests_and_never_reach_the_converter(
    string options
  )
  {
    FakeConverter converter = new((_, _) => throw new InvalidOperationException("unreachable"));
    await using var server = await RunningServer.StartAsync(converter);

    using var response = await server.PostAsync($$"""{"html":"<p>x</p>","options":{{options}}}""");

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestToken));
    await Assert
      .That(problem.RootElement.GetProperty("kind").GetString())
      .IsEqualTo(nameof(ConversionErrorKind.InvalidRequest));
    await Assert.That(converter.Calls).IsEqualTo(0);
  }

  [Test]
  [Arguments("1e12")]
  [Arguments("-1e12")]
  [Arguments("4294968")]
  [Arguments("-4294968")]
  [Arguments("1.7976931348623157e308")]
  // Request bodies may carry numbers as strings, named literals included.
  [Arguments("\"NaN\"")]
  [Arguments("\"Infinity\"")]
  [Arguments("\"-Infinity\"")]
  public async Task Wait_timeouts_out_of_range_are_bad_requests_and_never_reach_the_converter(
    string seconds
  )
  {
    FakeConverter converter = new((_, _) => throw new InvalidOperationException("unreachable"));
    await using var server = await RunningServer.StartAsync(converter);

    using var response = await server.PostAsync(
      $$$"""{"html":"<p>x</p>","options":{"waitForSignal":"ready","waitTimeoutSeconds":{{{seconds}}}}}"""
    );

    var detail = await AssertProblemAsync(response, 400, ConversionErrorKind.InvalidRequest);
    await Assert
      .That(detail)
      .StartsWith("waitTimeoutSeconds must be between 0 and 4294967 seconds");
    await Assert.That(converter.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task The_longest_wait_timeout_reaches_the_converter()
  {
    PdfOptions? received = null;
    await using var server = await RunningServer.StartAsync(
      new FakeConverter(
        (_, _) => Task.FromResult<ConversionError?>(null),
        options => received = options
      )
    );

    using var response = await server.PostAsync(
      """{"html":"<p>x</p>","options":{"waitForSignal":"ready","waitTimeoutSeconds":4294967}}"""
    );

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(received!.WaitTimeout).IsEqualTo(TimeSpan.FromSeconds(4_294_967));
  }

  [Test]
  public async Task Engine_limits_are_configurable_like_any_setting()
  {
    // Environment variables (ReportsEngine__Concurrency__MaxConcurrentConversions) and the command
    // line feed the same configuration section.
    await using var server = await RunningServer.StartAsync(
      converter: null,
      "--ReportsEngine:Concurrency:MaxConcurrentConversions=3",
      "--ReportsEngine:Concurrency:MaxQueueLength=7",
      "--ReportsEngine:ConversionTimeout=00:00:42"
    );

    var options = server.Services.GetRequiredService<IOptions<ReportsEngineOptions>>().Value;

    await Assert.That(options.Concurrency.MaxConcurrentConversions).IsEqualTo(3);
    await Assert.That(options.Concurrency.MaxQueueLength).IsEqualTo(7);
    await Assert.That(options.ConversionTimeout).IsEqualTo(TimeSpan.FromSeconds(42));
  }

  [Test]
  [NotInParallel("chrome")]
  public async Task Converts_html_with_the_real_engine()
  {
    await using var server = await RunningServer.StartAsync(
      converter: null,
      $"--ReportsEngine:Browser:CommandTimeout={TestEngine.GenerousTimeout}",
      "--ReportsEngine:Browser:StartupTimeout=00:01:00"
    );

    using var response = await server.PostAsync("""{"html":"<!DOCTYPE html><h1>Served</h1>"}""");

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    var pdf = await response.Content.ReadAsByteArrayAsync(TestToken);
    await Assert.That(PdfInspector.HasPdfHeader(pdf)).IsTrue();
    await Assert.That(PdfInspector.HasEofMarker(pdf)).IsTrue();
  }

  /// <summary>
  /// Asserts that <paramref name="response"/> is problem details with <paramref name="status"/> and
  /// <paramref name="kind"/>, and returns its detail.
  /// </summary>
  private static async Task<string?> AssertProblemAsync(
    HttpResponseMessage response,
    int status,
    ConversionErrorKind kind
  )
  {
    await Assert.That((int)response.StatusCode).IsEqualTo(status);
    await Assert
      .That(response.Content.Headers.ContentType?.MediaType)
      .IsEqualTo("application/problem+json");
    using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestToken));
    await Assert.That(problem.RootElement.GetProperty("status").GetInt32()).IsEqualTo(status);
    await Assert
      .That(problem.RootElement.GetProperty("kind").GetString())
      .IsEqualTo(kind.ToString());
    return problem.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
  }

  /// <summary>
  /// A body with <paramref name="contentType"/>, or with no <c>Content-Type</c> at all.
  /// </summary>
  private static ByteArrayContent Content(string body, string? contentType)
  {
    ByteArrayContent content = new(Encoding.UTF8.GetBytes(body));
    if (contentType is not null)
    {
      content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
    }

    return content;
  }
}
