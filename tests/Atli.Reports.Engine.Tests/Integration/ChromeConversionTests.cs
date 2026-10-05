using Atli.Reports.Engine.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Core.Interfaces;

namespace Atli.Reports.Engine.Tests.Integration;

/// <summary>
/// One engine, and so one browser, shared by every test that only needs a working converter.
/// </summary>
public sealed class SharedChromeEngine : IAsyncInitializer, IAsyncDisposable
{
  private ServiceProvider? _provider;

  public IHtmlToPdfConverter Converter => _provider!.GetRequiredService<IHtmlToPdfConverter>();

  public Task InitializeAsync()
  {
    _provider = TestEngine.Create();
    return Task.CompletedTask;
  }

  public async ValueTask DisposeAsync()
  {
    if (_provider is not null)
    {
      await _provider.DisposeAsync();
    }
  }
}

/// <summary>
/// Converts real HTML in the Chrome (or Chromium) installed on the machine, through one shared engine.
/// </summary>
/// <remarks>
/// Browser tests run one at a time (<c>[NotInParallel("chrome")]</c>): several of them start their own
/// browsers, and parallel browser start-ups on a two-core CI runner are slow enough to blur the
/// outcomes under test.
/// </remarks>
[NotInParallel("chrome")]
[ClassDataSource<SharedChromeEngine>(Shared = SharedType.PerTestSession)]
public class ChromeConversionTests(SharedChromeEngine engine)
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Converts_html_to_a_pdf()
  {
    var pdf = await engine.Converter.ConvertToBytesAsync(
      "<!DOCTYPE html><html><body><h1>Hello, PDF</h1></body></html>"
    );

    await Assert.That(PdfInspector.HasPdfHeader(pdf)).IsTrue();
    await Assert.That(PdfInspector.HasEofMarker(pdf)).IsTrue();
  }

  [Test]
  public async Task Writes_the_pdf_to_a_destination_stream()
  {
    using MemoryStream destination = new();

    var result = await engine.Converter.ConvertAsync(
      "<p>Streamed</p>",
      destination,
      new PdfOptions { PaperSize = PaperSize.A4, Orientation = PageOrientation.Landscape },
      TestToken
    );

    await Assert.That(result.IsT0).IsTrue().Because(TestEngine.Describe(result.Value));
    await Assert.That(PdfInspector.HasPdfHeader(destination.ToArray())).IsTrue();
    await Assert.That(PdfInspector.HasEofMarker(destination.ToArray())).IsTrue();
  }

  [Test]
  public async Task Waits_for_the_page_to_signal_before_printing()
  {
    var pdf = await engine.Converter.ConvertToBytesAsync(
      """
      <!DOCTYPE html>
      <html><head><title>Loading</title></head><body>
      <script>
        setTimeout(function () {
          document.title = "Ready";
          window.pdfReady();
        }, 100);
      </script>
      </body></html>
      """,
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TestEngine.GenerousTimeout }
    );

    await Assert.That(PdfInspector.ReadTitle(pdf)).IsEqualTo("Ready");
  }

  [Test]
  public async Task A_signal_keeps_the_document_in_standards_mode()
  {
    // The page signals only in standards mode. The signal used to be injected as a <script> before
    // the doctype, which switched every document that waited for a signal to quirks mode.
    var pdf = await engine.Converter.ConvertToBytesAsync(
      "<!DOCTYPE html><html><body><script>if (document.compatMode === 'CSS1Compat') window.done();</script></body></html>",
      new PdfOptions { WaitForSignal = "done", WaitTimeout = TestEngine.GenerousTimeout }
    );

    await Assert.That(PdfInspector.HasPdfHeader(pdf)).IsTrue();
  }

  [Test]
  public async Task Documents_without_a_signal_render_in_standards_mode()
  {
    var pdf = await engine.Converter.ConvertToBytesAsync(
      "<!DOCTYPE html><html><head><title>x</title></head><body><script>document.title = document.compatMode;</script></body></html>"
    );

    await Assert.That(PdfInspector.ReadTitle(pdf)).IsEqualTo("CSS1Compat");
  }

  [Test]
  public async Task Without_a_signal_the_engine_waits_for_the_load_event()
  {
    await using var provider = TestEngine.Create(options =>
      options.Network.Mode = ReportsEngineNetworkMode.Unrestricted
    );
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    await using TestHttpServer server = new();
    server.Map(
      "/slow.svg",
      """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>""",
      "image/svg+xml",
      delay: TimeSpan.FromMilliseconds(700)
    );

    var pdf = await converter.ConvertToBytesAsync(
      $$"""
      <!DOCTYPE html><html><head><title>loading</title></head><body>
      <img src="{{server.BaseUrl}}/slow.svg">
      <script>window.addEventListener('load', function () { document.title = 'loaded'; });</script>
      </body></html>
      """
    );

    await Assert.That(PdfInspector.ReadTitle(pdf)).IsEqualTo("loaded");
  }

  [Test]
  public async Task The_signal_survives_navigation_to_another_document()
  {
    await using var provider = TestEngine.Create(options =>
      options.Network.Mode = ReportsEngineNetworkMode.Unrestricted
    );
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    await using TestHttpServer server = new();
    server.Map(
      "/next",
      "<!DOCTYPE html><html><head><title>next</title></head><body><script>window.pdfReady();</script></body></html>"
    );

    var pdf = await converter.ConvertToBytesAsync(
      $"<script>location.href = '{server.BaseUrl}/next';</script>",
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TestEngine.GenerousTimeout }
    );

    await Assert.That(PdfInspector.ReadTitle(pdf)).IsEqualTo("next");
  }

  [Test]
  public async Task Times_out_when_the_page_never_signals()
  {
    var result = await engine.Converter.ConvertAsync(
      "<p>Never ready</p>",
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TimeSpan.FromMilliseconds(500) },
      TestToken
    );

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.SignalTimeout);
  }

  [Test]
  public async Task Print_options_the_browser_rejects_are_RenderFailed_and_write_nothing()
  {
    using MemoryStream destination = new();

    var result = await engine.Converter.ConvertAsync(
      "<p>One page</p>",
      destination,
      new PdfOptions { PageRanges = "not-a-range" },
      TestToken
    );

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert
      .That(result.AsT1.Message)
      .StartsWith("PDF generation failed: Page.printToPDF failed:");
    await Assert.That(destination.Length).IsEqualTo(0);
  }

  [Test]
  public async Task Canceling_while_waiting_for_the_signal_reports_Canceled()
  {
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
    cancellation.CancelAfter(TimeSpan.FromSeconds(1));

    var result = await engine.Converter.ConvertAsync(
      "<p>Never ready</p>",
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TestEngine.GenerousTimeout },
      cancellation.Token
    );

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Canceled);
  }

  [Test]
  public async Task Converts_html_larger_than_a_megabyte()
  {
    var html = TestDocuments.LargeImageDocument();
    await Assert.That(html.Length).IsGreaterThan(1024 * 1024);
    using MemoryStream destination = new();

    var result = await engine.Converter.ConvertAsync(html, destination, null, TestToken);

    await Assert.That(result.IsT0).IsTrue().Because(TestEngine.Describe(result.Value));
    var pdf = destination.ToArray();
    await Assert.That(PdfInspector.HasPdfHeader(pdf)).IsTrue();
    await Assert.That(PdfInspector.HasEofMarker(pdf)).IsTrue();
    // The bitmap is noise and does not compress, so the PDF itself spans many WebSocket frames.
    await Assert.That(pdf.Length).IsGreaterThan(500 * 1024);
  }

  [Test]
  public async Task Tagging_can_be_turned_off()
  {
    var html =
      "<!DOCTYPE html><html><body><h1>Heading</h1><table><tr><td>Cell</td></tr></table></body></html>";

    var untagged = await engine.Converter.ConvertToBytesAsync(
      html,
      new PdfOptions { GenerateTaggedPdf = false }
    );
    var tagged = await engine.Converter.ConvertToBytesAsync(
      html,
      new PdfOptions { GenerateTaggedPdf = true }
    );

    await Assert
      .That(System.Text.Encoding.Latin1.GetString(untagged))
      .DoesNotContain("/StructTreeRoot");
    await Assert.That(System.Text.Encoding.Latin1.GetString(tagged)).Contains("/StructTreeRoot");
  }
}

/// <summary>
/// Converter behavior that needs an engine configured for the test, so each test owns its engine.
/// </summary>
[NotInParallel("chrome")]
public class ChromeEngineConfigurationTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Streams_a_pdf_of_more_than_a_hundred_pages_chunk_by_chunk()
  {
    await using var provider = TestEngine.Create(options =>
      options.Browser.PdfReadChunkSize = 16 * 1024
    );
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    RecordingStream destination = new();

    var result = await converter.ConvertAsync(
      TestDocuments.ManyPagesDocument(150),
      destination,
      null,
      TestToken
    );

    await Assert.That(result.IsT0).IsTrue().Because(TestEngine.Describe(result.Value));
    var pdf = destination.ToArray();
    await Assert.That(PdfInspector.HasPdfHeader(pdf)).IsTrue();
    await Assert.That(PdfInspector.HasEofMarker(pdf)).IsTrue();
    await Assert.That(PdfInspector.CountPages(pdf)).IsEqualTo(150);
    await Assert.That(destination.Writes).IsGreaterThan(1);
  }

  [Test]
  public async Task Twenty_parallel_conversions_share_four_slots()
  {
    await using var provider = TestEngine.Create(options =>
    {
      options.Concurrency.MaxConcurrentConversions = 4;
      options.Concurrency.QueueTimeout = TimeSpan.FromMinutes(2);
    });
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();

    var results = await Task.WhenAll(
      Enumerable
        .Range(1, 20)
        .Select(index =>
          converter
            .ConvertAsync($"<h1>Document {index}</h1>", cancellationToken: TestToken)
            .AsTask()
        )
    );

    foreach (var result in results)
    {
      await Assert.That(result.IsT0).IsTrue().Because(TestEngine.Describe(result.Value));
      await result.AsT0.DisposeAsync();
    }

    var limiter = provider.GetRequiredService<Atli.Reports.Engine.Conversion.ConversionLimiter>();
    await Assert.That(limiter.PeakActive).IsLessThanOrEqualTo(4);
    await Assert.That(limiter.Active).IsEqualTo(0);
  }

  [Test]
  public async Task A_full_queue_is_Busy_and_queued_conversions_still_run()
  {
    await using var provider = TestEngine.Create(options =>
    {
      options.Concurrency.MaxConcurrentConversions = 1;
      options.Concurrency.MaxQueueLength = 1;
    });
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    var limiter = provider.GetRequiredService<Atli.Reports.Engine.Conversion.ConversionLimiter>();
    using var cancelBlocker = CancellationTokenSource.CreateLinkedTokenSource(TestToken);

    var blocker = converter
      .ConvertAsync(
        "<p>Never ready</p>",
        new PdfOptions { WaitForSignal = "never", WaitTimeout = TestEngine.GenerousTimeout },
        cancelBlocker.Token
      )
      .AsTask();
    await Assert
      .That(
        await TestEngine.EventuallyAsync(
          () => Task.FromResult(limiter.Active == 1),
          TestEngine.GenerousTimeout
        )
      )
      .IsTrue();
    var queued = converter.ConvertAsync("<p>Queued</p>", cancellationToken: TestToken).AsTask();
    await Assert
      .That(
        await TestEngine.EventuallyAsync(
          () => Task.FromResult(limiter.Queued == 1),
          TestEngine.GenerousTimeout
        )
      )
      .IsTrue();

    var rejected = await converter.ConvertAsync("<p>Rejected</p>", cancellationToken: TestToken);
    await cancelBlocker.CancelAsync();

    await Assert.That(rejected.AsT1.Kind).IsEqualTo(ConversionErrorKind.Busy);
    await Assert.That((await blocker).AsT1.Kind).IsEqualTo(ConversionErrorKind.Canceled);
    var queuedResult = await queued;
    await Assert.That(queuedResult.IsT0).IsTrue().Because(TestEngine.Describe(queuedResult.Value));
    await queuedResult.AsT0.DisposeAsync();
  }

  [Test]
  public async Task Health_checks_report_the_browser_and_recent_conversions()
  {
    ServiceCollection services = new();
    services.AddReportsEngine(TestEngine.ConfigureForTests);
    services.AddHealthChecks().AddReportsEngineBrowserCheck().AddReportsEngineConversionCheck();
    await using var provider = services.BuildServiceProvider();

    var converted = await provider
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync("<p>Healthy</p>", cancellationToken: TestToken);
    if (converted.TryPickT0(out var pdf, out _))
    {
      await pdf.DisposeAsync();
    }

    var report = await provider
      .GetRequiredService<HealthCheckService>()
      .CheckHealthAsync(TestToken);

    await Assert
      .That(report.Entries[ReportsEngineHealthChecksBuilderExtensions.BrowserCheckName].Status)
      .IsEqualTo(HealthStatus.Healthy);
    await Assert
      .That(
        report.Entries[ReportsEngineHealthChecksBuilderExtensions.ConversionCheckName].Description
      )
      .IsEqualTo("1/1 succeeded (100 %)");
  }

  /// <summary>
  /// A write-only stream that keeps what it receives and counts the writes.
  /// </summary>
  private sealed class RecordingStream : MemoryStream
  {
    public int Writes { get; private set; }

    public override ValueTask WriteAsync(
      ReadOnlyMemory<byte> buffer,
      CancellationToken cancellationToken = default
    )
    {
      Writes++;
      return base.WriteAsync(buffer, cancellationToken);
    }
  }
}
