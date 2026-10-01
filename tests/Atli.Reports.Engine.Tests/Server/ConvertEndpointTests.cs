using System.Net;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Server;
using Atli.Reports.Server.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OneOf;
using OneOf.Types;

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
  public async Task A_failure_after_the_first_byte_aborts_the_response()
  {
    await using var server = await RunningServer.StartAsync(
      new FakeConverter(
        async (destination, cancellationToken) =>
        {
          await destination.WriteAsync(new byte[64 * 1024], cancellationToken);
          await destination.FlushAsync(cancellationToken);
          return new ConversionError(ConversionErrorKind.BrowserUnavailable, "The browser died.");
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
      {"html":"<p>x</p>","options":{"paperSize":"a4","paperWidth":5.5,"paperHeight":7.25,"generateTaggedPdf":false,"waitForSignal":"ready","waitTimeoutSeconds":-0.001}}
      """
    );

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(received!.PaperSize).IsEqualTo(new PaperSize { Width = 5.5, Height = 7.25 });
    await Assert.That(received.GenerateTaggedPdf).IsFalse();
    await Assert.That(received.WaitTimeout).IsEqualTo(Timeout.InfiniteTimeSpan);
  }

  [Test]
  public async Task Omitted_tagging_leaves_the_choice_to_the_browser()
  {
    PdfOptions? received = null;
    await using var server = await RunningServer.StartAsync(
      new FakeConverter(
        (_, _) => Task.FromResult<ConversionError?>(null),
        options => received = options
      )
    );

    using var response = await server.PostAsync(
      """{"html":"<p>x</p>","options":{"paperSize":"Legal"}}"""
    );

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(received!.PaperSize).IsEqualTo(PaperSize.Legal);
    await Assert.That(received.GenerateTaggedPdf).IsNull();
  }

  [Test]
  [Arguments("""{"paperSize":"tabloid"}""")]
  [Arguments("""{"paperWidth":5}""")]
  [Arguments("""{"paperHeight":7}""")]
  [Arguments("""{"paperWidth":0,"paperHeight":7}""")]
  [Arguments("""{"paperWidth":5,"paperHeight":-1}""")]
  public async Task Invalid_paper_sizes_are_bad_requests_and_never_reach_the_converter(
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
  /// A converter whose streaming overload runs <paramref name="write"/>; a non-null result is the error.
  /// </summary>
  private sealed class FakeConverter(
    Func<Stream, CancellationToken, Task<ConversionError?>> write,
    Action<PdfOptions?>? inspect = null
  ) : IHtmlToPdfConverter
  {
    public int Calls { get; private set; }

    public ValueTask<OneOf<Stream, ConversionError>> ConvertAsync(
      string html,
      PdfOptions? options = null,
      CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("The server streams.");

    public async ValueTask<OneOf<Success, ConversionError>> ConvertAsync(
      string html,
      Stream destination,
      PdfOptions? options = null,
      CancellationToken cancellationToken = default
    )
    {
      Calls++;
      inspect?.Invoke(options);
      var error = await write(destination, cancellationToken);
      return error is null ? new Success() : error;
    }
  }

  private sealed class RunningServer(WebApplication app) : IAsyncDisposable
  {
    public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.First()) };

    public IServiceProvider Services => app.Services;

    public static async Task<RunningServer> StartAsync(
      IHtmlToPdfConverter? converter,
      params string[] arguments
    )
    {
      var app = ReportsServerApplication.Create(
        [
          "--urls=http://127.0.0.1:0",
          "--ReportsEngine:Browser:WarmUpOnStartup=false",
          "--ReportsEngine:Browser:NoSandbox=true",
          .. arguments,
        ],
        builder =>
        {
          builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
          if (converter is not null)
          {
            builder.Services.AddSingleton(converter);
          }
        }
      );
      await app.StartAsync(TestToken);
      return new RunningServer(app);
    }

    public Task<HttpResponseMessage> PostAsync(string json) =>
      Client.PostAsync(
        "/convert",
        new StringContent(json, Encoding.UTF8, "application/json"),
        TestToken
      );

    public async ValueTask DisposeAsync()
    {
      Client.Dispose();
      await app.StopAsync(CancellationToken.None);
      await app.DisposeAsync();
    }
  }
}
