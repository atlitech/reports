using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Engine.Tests.Integration;

/// <summary>
/// Converts real HTML in the Chrome (or Chromium) installed on the machine.
/// </summary>
/// <remarks>
/// The browser runs without its sandbox: Ubuntu 24.04 runners block the user namespaces the sandbox
/// needs, and the HTML here is trusted.
/// </remarks>
public class ChromeConversionTests
{
  private static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(20);

  [Test]
  public async Task Converts_html_to_a_pdf()
  {
    await using var provider = CreateProvider();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();

    var result = await converter.ConvertAsync(
      "<!DOCTYPE html><html><body><h1>Hello, PDF</h1></body></html>",
      cancellationToken: TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(result.IsT0).IsTrue().Because(Describe(result.Value));
    await using var pdf = result.AsT0;
    await Assert.That(await ReadHeaderAsync(pdf)).IsEqualTo("%PDF-");
  }

  [Test]
  public async Task Writes_the_pdf_to_a_destination_stream()
  {
    await using var provider = CreateProvider();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    using MemoryStream destination = new();

    var result = await converter.ConvertAsync(
      "<p>Streamed</p>",
      destination,
      new PdfOptions { PaperSize = PaperSize.A4, Orientation = PageOrientation.Landscape },
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(result.IsT0).IsTrue().Because(Describe(result.Value));
    destination.Position = 0;
    await Assert.That(await ReadHeaderAsync(destination)).IsEqualTo("%PDF-");
  }

  [Test]
  public async Task Waits_for_the_page_to_signal_before_printing()
  {
    await using var provider = CreateProvider();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();

    var result = await converter.ConvertAsync(
      """
      <p id="status">Loading…</p>
      <script>
        setTimeout(function () {
          document.getElementById("status").textContent = "Ready";
          window.pdfReady();
        }, 100);
      </script>
      """,
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = GenerousTimeout },
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(result.IsT0).IsTrue().Because(Describe(result.Value));
    await using var pdf = result.AsT0;
    await Assert.That(await ReadHeaderAsync(pdf)).IsEqualTo("%PDF-");
  }

  [Test]
  public async Task Times_out_when_the_page_never_signals()
  {
    await using var provider = CreateProvider();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();

    var result = await converter.ConvertAsync(
      "<p>Never ready</p>",
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TimeSpan.FromMilliseconds(500) },
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.SignalTimeout);
  }

  [Test]
  public async Task Print_options_the_browser_rejects_are_RenderFailed()
  {
    await using var provider = CreateProvider();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();

    var result = await converter.ConvertAsync(
      "<p>One page</p>",
      new PdfOptions { PageRanges = "not-a-range" },
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert
      .That(result.AsT1.Message)
      .StartsWith("PDF generation failed: Page.printToPDF failed:");
  }

  [Test]
  public async Task Canceling_while_waiting_for_the_signal_reports_Canceled()
  {
    await using var provider = CreateProvider();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
      TestContext.Current!.Execution.CancellationToken
    );
    cancellation.CancelAfter(TimeSpan.FromSeconds(1));

    var result = await converter.ConvertAsync(
      "<p>Never ready</p>",
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = GenerousTimeout },
      cancellation.Token
    );

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Canceled);
  }

  [Test]
  public async Task Health_checks_report_the_browser_and_recent_conversions()
  {
    ServiceCollection services = new();
    services.AddReportsEngine(ConfigureForTests);
    services.AddHealthChecks().AddReportsEngineBrowserCheck().AddReportsEngineConversionCheck();
    await using var provider = services.BuildServiceProvider();
    var cancellationToken = TestContext.Current!.Execution.CancellationToken;

    var converted = await provider
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync("<p>Healthy</p>", cancellationToken: cancellationToken);
    if (converted.TryPickT0(out var pdf, out _))
    {
      await pdf.DisposeAsync();
    }

    var report = await provider
      .GetRequiredService<HealthCheckService>()
      .CheckHealthAsync(cancellationToken);

    await Assert
      .That(report.Entries[ReportsEngineHealthChecksBuilderExtensions.BrowserCheckName].Status)
      .IsEqualTo(HealthStatus.Healthy);
    await Assert
      .That(
        report.Entries[ReportsEngineHealthChecksBuilderExtensions.ConversionCheckName].Description
      )
      .IsEqualTo("1/1 succeeded (100 %)");
  }

  private static ServiceProvider CreateProvider()
  {
    ServiceCollection services = new();
    services.AddReportsEngine(ConfigureForTests);
    return services.BuildServiceProvider();
  }

  private static void ConfigureForTests(ReportsEngineOptions options)
  {
    options.Browser.NoSandbox = true;
    options.Browser.DisableDevShmUsage = true;
    options.Browser.CommandTimeout = GenerousTimeout;
  }

  private static async Task<string> ReadHeaderAsync(Stream pdf)
  {
    var header = new byte[5];
    await pdf.ReadExactlyAsync(header);
    return Encoding.ASCII.GetString(header);
  }

  private static string Describe(object value) =>
    value is ConversionError error
      ? $"{error.Kind}: {error.Message} {error.Exception}"
      : "succeeded";
}
