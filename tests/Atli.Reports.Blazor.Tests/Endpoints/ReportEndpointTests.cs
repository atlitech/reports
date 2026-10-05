using System.Net;
using System.Net.Http.Json;
using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Endpoints;

/// <summary>
/// Covers how <c>MapBlazorReport</c> answers, with a fake converter standing in for the browser.
/// </summary>
public class ReportEndpointTests
{
  [Test]
  [Arguments(ConversionErrorKind.InvalidRequest, HttpStatusCode.BadRequest)]
  [Arguments(ConversionErrorKind.Busy, HttpStatusCode.ServiceUnavailable)]
  [Arguments(ConversionErrorKind.BrowserUnavailable, HttpStatusCode.ServiceUnavailable)]
  [Arguments(ConversionErrorKind.Timeout, HttpStatusCode.GatewayTimeout)]
  [Arguments(ConversionErrorKind.SignalTimeout, HttpStatusCode.GatewayTimeout)]
  [Arguments(ConversionErrorKind.Canceled, (HttpStatusCode)499)]
  [Arguments(ConversionErrorKind.RenderFailed, HttpStatusCode.InternalServerError)]
  [Arguments(ConversionErrorKind.Unauthorized, HttpStatusCode.InternalServerError)]
  [Arguments(ConversionErrorKind.Forbidden, HttpStatusCode.InternalServerError)]
  [Arguments(ConversionErrorKind.PolicyDenied, HttpStatusCode.UnprocessableEntity)]
  public async Task Conversion_errors_become_problem_responses(
    ConversionErrorKind kind,
    HttpStatusCode expectedStatus
  )
  {
    await using var server = await StartAsync(FakeHtmlToPdfConverter.Failing(kind));

    using var response = await server.Client.PostAsJsonAsync(
      "/greetingreport",
      new GreetingData("error"),
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(response.StatusCode).IsEqualTo(expectedStatus);
    await Assert
      .That(response.Content.Headers.ContentType?.MediaType)
      .IsEqualTo("application/problem+json");
    await Assert.That(response.Content.Headers.ContentDisposition).IsNull();
    var problem = await response.Content.ReadFromJsonAsync<ProblemBody>();
    await Assert.That(problem?.Status).IsEqualTo((int)expectedStatus);
    await Assert.That(problem?.Title).IsNotNull();
    await Assert.That(problem?.Kind).IsEqualTo(kind.ToString());
  }

  [Test]
  public async Task A_failure_after_part_of_the_pdf_was_sent_breaks_the_response()
  {
    // Large enough that the first write flushes the response headers with a 200 status.
    var partialPdf = new byte[256 * 1024];
    await using var server = await StartAsync(
      FakeHtmlToPdfConverter.Failing(ConversionErrorKind.RenderFailed, partialPdf)
    );

    await Assert
      .That(async () =>
        await server.Client.PostAsJsonAsync(
          "/greetingreport",
          new GreetingData("partial"),
          TestContext.Current!.Execution.CancellationToken
        )
      )
      .Throws<HttpRequestException>();
  }

  [Test]
  public async Task Pdf_options_reach_the_engine()
  {
    var converter = FakeHtmlToPdfConverter.Succeeding();
    await using var server = await StartAsync(
      converter,
      options =>
      {
        options.PdfOptions.Orientation = PageOrientation.Landscape;
        options.PdfOptions.PaperSize = PaperSize.A4;
        options.PdfOptions.Margins = new Margins
        {
          Top = 1,
          Right = 0.5,
          Bottom = 0.25,
          Left = 0,
        };
        options.PdfOptions.PrintBackground = false;
        options.PdfOptions.Scale = 0.8;
        options.PdfOptions.HeaderTemplate = "<span class=\"title\"></span>";
        options.PdfOptions.FooterTemplate = "<span class=\"pageNumber\"></span>";
        options.PdfOptions.DisplayHeaderFooter = true;
        options.PdfOptions.PageRanges = "1-2";
        options.PdfOptions.PreferCssPageSize = true;
        options.PdfOptions.GenerateTaggedPdf = false;
      }
    );

    using var response = await server.Client.PostAsJsonAsync(
      "/greetingreport",
      new GreetingData("settings"),
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    var options = converter.LastOptions!;
    await Assert.That(options.Orientation).IsEqualTo(PageOrientation.Landscape);
    await Assert.That(options.PaperSize).IsEqualTo(new PaperSize { Width = 8.27, Height = 11.69 });
    await Assert
      .That(options.Margins)
      .IsEqualTo(
        new Margins
        {
          Top = 1,
          Right = 0.5,
          Bottom = 0.25,
          Left = 0,
        }
      );
    await Assert.That(options.PrintBackground).IsFalse();
    await Assert.That(options.Scale).IsEqualTo(0.8);
    await Assert.That(options.HeaderTemplate).IsEqualTo("<span class=\"title\"></span>");
    await Assert.That(options.FooterTemplate).IsEqualTo("<span class=\"pageNumber\"></span>");
    await Assert.That(options.DisplayHeaderFooter).IsTrue();
    await Assert.That(options.PageRanges).IsEqualTo("1-2");
    await Assert.That(options.PreferCssPageSize).IsTrue();
    await Assert.That(options.GenerateTaggedPdf).IsFalse();
    await Assert.That(options.WaitForSignal).IsNull();
    await Assert.That(converter.LastHtml).Contains("<h1>Hello, settings</h1>");
  }

  private static Task<TestReportServer> StartAsync(
    FakeHtmlToPdfConverter converter,
    Action<BlazorReportOptions>? configureReports = null
  )
  {
    return TestReportServer.StartAsync(
      app => app.MapBlazorReport<GreetingReport, GreetingData>(),
      configureReports,
      services => services.AddSingleton<IHtmlToPdfConverter>(converter)
    );
  }

  private sealed record ProblemBody(int? Status, string? Title, string? Kind);
}
