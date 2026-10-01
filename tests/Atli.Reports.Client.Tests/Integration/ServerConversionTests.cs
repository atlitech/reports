using Atli.Reports.Client.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Client.Tests.Integration;

/// <summary>
/// Converts through the client against the real server, which renders in a real browser.
/// </summary>
/// <remarks>
/// Browser tests run one at a time (<c>[NotInParallel("chrome")]</c>), like the engine's own, because
/// parallel browser start-ups on a two-core CI runner are slow enough to blur the outcomes.
/// </remarks>
[NotInParallel("chrome")]
[ClassDataSource<RunningReportsServer>(Shared = SharedType.PerTestSession)]
public class ServerConversionTests(RunningReportsServer server)
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Converts_html_into_the_destination_stream()
  {
    await using var client = CreateClient();
    using MemoryStream destination = new();

    var result = await client
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync("<!DOCTYPE html><h1>Remote</h1>", destination, null, TestToken);

    await Assert.That(result.IsT0).IsTrue().Because(Describe(result.Value));
    await Assert.That(Pdf.IsComplete(destination.ToArray())).IsTrue();
  }

  [Test]
  public async Task Returns_the_pdf_as_a_stream()
  {
    await using var client = CreateClient();

    var result = await client
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync("<!DOCTYPE html><h1>Remote</h1>", cancellationToken: TestToken);

    await Assert.That(result.IsT0).IsTrue().Because(Describe(result.Value));
    await using var pdf = result.AsT0;
    using MemoryStream copy = new();
    await pdf.CopyToAsync(copy, TestToken);
    await Assert.That(Pdf.IsComplete(copy.ToArray())).IsTrue();
  }

  [Test]
  public async Task Page_options_reach_the_browser()
  {
    await using var client = CreateClient();
    using MemoryStream destination = new();

    var result = await client
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync(
        """
        <!DOCTYPE html><style>section{page-break-after:always}</style>
        <section>One</section><section>Two</section><section>Three</section>
        """,
        destination,
        new PdfOptions
        {
          PaperSize = new PaperSize { Width = 5, Height = 7 },
          Orientation = PageOrientation.Landscape,
          PageRanges = "2-3",
          GenerateTaggedPdf = false,
        },
        TestToken
      );

    await Assert.That(result.IsT0).IsTrue().Because(Describe(result.Value));
    var pdf = destination.ToArray();
    await Assert.That(Pdf.CountPages(pdf)).IsEqualTo(2);
    // 5 x 7 inches, landscape: 504 x 360 points.
    var sizes = Pdf.PageSizes(pdf);
    await Assert.That(sizes).IsNotEmpty();
    await Assert.That(sizes.Distinct().Single()).IsEqualTo((504d, 360d));
  }

  [Test]
  public async Task A_document_that_never_signals_is_a_signal_timeout()
  {
    await using var client = CreateClient();

    var result = await client
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync(
        "<!DOCTYPE html><p>Never ready</p>",
        Stream.Null,
        new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TimeSpan.FromMilliseconds(300) },
        TestToken
      );

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.SignalTimeout);
  }

  [Test]
  public async Task A_document_that_signals_is_printed_after_an_infinite_wait()
  {
    await using var client = CreateClient();
    using MemoryStream destination = new();

    var result = await client
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync(
        "<!DOCTYPE html><p>Ready</p><script>setTimeout(() => window.pdfReady(), 50)</script>",
        destination,
        new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = Timeout.InfiniteTimeSpan },
        TestToken
      );

    await Assert.That(result.IsT0).IsTrue().Because(Describe(result.Value));
    await Assert.That(Pdf.IsComplete(destination.ToArray())).IsTrue();
  }

  [Test]
  public async Task Blank_html_is_an_invalid_request()
  {
    await using var client = CreateClient();

    var result = await client
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync("  ", Stream.Null, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.InvalidRequest);
  }

  [Test]
  public async Task The_health_check_sees_a_ready_server()
  {
    await using var client = CreateClient();

    var report = await client.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestToken);

    await Assert.That(report.Status).IsEqualTo(HealthStatus.Healthy);
  }

  private ServiceProvider CreateClient()
  {
    ServiceCollection services = new();
    services.AddReportsClient(new ReportsClientSettings { Endpoint = server.Endpoint });
    return services.BuildServiceProvider();
  }

  private static string Describe(object value) =>
    value is ConversionError error ? $"{error.Kind}: {error.Message}" : "succeeded";
}
