using System.Net;
using System.Net.Http.Json;
using System.Text;
using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Integration;

/// <summary>
/// HTML reports never reach the engine, so they work on machines without a browser. Every test here
/// points the engine at a browser that does not exist to prove it.
/// </summary>
public class HtmlReportTests
{
  private static readonly FileInfo MissingBrowser = new(
    Path.Combine(Path.GetTempPath(), "atli-reports-tests", "no-such-browser")
  );

  [Test]
  public async Task Renders_an_html_report_without_a_browser()
  {
    await using var services = TestEngine.CreateServices(configureServices: UseMissingBrowser);
    var report = services
      .GetRequiredService<BlazorReportRegistry>()
      .AddReport<GreetingReport>(options => options.OutputFormat = ReportOutputFormat.Html);
    using MemoryStream destination = new();

    var result = await services
      .GetRequiredService<IReportService>()
      .GenerateReport(
        destination,
        report,
        new GreetingData("HTML"),
        TestContext.Current!.Execution.CancellationToken
      );

    var html = Encoding.UTF8.GetString(destination.ToArray());
    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(html).StartsWith("<!DOCTYPE html>");
    await Assert.That(html).Contains("<h1>Hello, HTML</h1>");
  }

  [Test]
  public async Task The_mapped_endpoint_returns_the_html()
  {
    await using var server = await TestReportServer.StartAsync(
      app =>
        app.MapBlazorReport<GreetingReport, GreetingData>(options =>
          options.OutputFormat = ReportOutputFormat.Html
        ),
      configureServices: UseMissingBrowser
    );

    using var response = await server.Client.PostAsJsonAsync(
      "/greetingreport",
      new GreetingData("endpoint"),
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
    await Assert
      .That(response.Content.Headers.ContentDisposition?.FileName)
      .IsEqualTo("GreetingReport.html");
    await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Hello, endpoint");
  }

  [Test]
  public async Task A_pdf_report_without_a_browser_is_503_BrowserUnavailable()
  {
    await using var server = await TestReportServer.StartAsync(
      app => app.MapBlazorReport<StaticReport>(),
      configureServices: UseMissingBrowser
    );

    using var response = await server.Client.PostAsync(
      "/staticreport",
      content: null,
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    await Assert
      .That(response.Content.Headers.ContentType?.MediaType)
      .IsEqualTo("application/problem+json");
    await Assert.That(response.Content.Headers.ContentDisposition).IsNull();
  }

  private static void UseMissingBrowser(IServiceCollection services) =>
    services.AddReportsEngine(options => options.Browser.ExecutablePath = MissingBrowser.FullName);
}
