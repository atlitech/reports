using System.Net;
using System.Net.Http.Json;
using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Integration;

/// <summary>
/// Renders reports to PDF in the Chrome (or Chromium) installed on the machine.
/// </summary>
/// <remarks>
/// The tests run one at a time, like the engine's own Chrome tests, because each conversion launches a
/// browser and parallel cold starts on a two-core CI runner are slow enough to blur the outcomes.
/// </remarks>
[NotInParallel("chrome")]
public class PdfReportTests
{
  [Test]
  public async Task Renders_a_component_report_to_a_pdf()
  {
    await using var services = TestEngine.CreateServices();
    var report = services
      .GetRequiredService<BlazorReportRegistry>()
      .AddReport<GreetingReport, GreetingData>();
    var reportService = services.GetRequiredService<IReportService>();
    using MemoryStream destination = new();

    var result = await reportService.GenerateReport(
      destination,
      report,
      new GreetingData("PDF"),
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(result.IsT0).IsTrue().Because(Describe(result.Value));
    await Assert.That(Pdf.ReadHeader(destination.ToArray())).IsEqualTo(Pdf.Header);
  }

  [Test]
  public async Task The_mapped_endpoint_streams_the_pdf_to_the_response()
  {
    await using var server = await TestReportServer.StartAsync(app =>
      app.MapBlazorReport<GreetingReport, GreetingData>()
    );

    using var response = await server.Client.PostAsJsonAsync(
      "/greetingreport",
      new GreetingData("endpoint"),
      TestContext.Current!.Execution.CancellationToken
    );

    var body = await response.Content.ReadAsByteArrayAsync();
    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/pdf");
    await Assert
      .That(response.Content.Headers.ContentDisposition?.FileName)
      .IsEqualTo("GreetingReport.pdf");
    await Assert.That(Pdf.ReadHeader(body)).IsEqualTo(Pdf.Header);
  }

#pragma warning disable CS0618 // Covers the obsolete PipeWriter overloads kept for BlazorReports callers.
  [Test]
  public async Task The_obsolete_pipe_writer_overload_still_writes_and_completes_the_pdf()
  {
    await using var services = TestEngine.CreateServices();
    var report = services.GetRequiredService<BlazorReportRegistry>().AddReport<StaticReport>();
    var reportService = services.GetRequiredService<IReportService>();
    System.IO.Pipelines.Pipe pipe = new(
      new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 0)
    );

    var result = await reportService.GenerateReport(
      pipe.Writer,
      report,
      TestContext.Current!.Execution.CancellationToken
    );
    using MemoryStream document = new();
    await pipe.Reader.AsStream().CopyToAsync(document);

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(Pdf.ReadHeader(document.ToArray())).IsEqualTo(Pdf.Header);
  }
#pragma warning restore CS0618

  private static string Describe(object value) =>
    value is ConversionError error
      ? $"{error.Kind}: {error.Message} {error.Exception}"
      : "succeeded";
}
