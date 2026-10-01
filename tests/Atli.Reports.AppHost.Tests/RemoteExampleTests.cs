using System.Net.Http.Json;
using Atli.Reports.AppHost.Tests.Support;

namespace Atli.Reports.AppHost.Tests;

/// <summary>
/// examples/RemoteReportServer renders Blazor reports in its own process, converts them on the reports
/// server through Atli.Reports.Client, and never starts a browser.
/// </summary>
[ClassDataSource<ReportsAppHost>(Shared = SharedType.PerTestSession)]
public class RemoteExampleTests(ReportsAppHost appHost)
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Renders_a_blazor_report_that_the_server_converts()
  {
    var pdf = await RenderReportAsync();

    await Assert.That(Pdf.IsComplete(pdf)).IsTrue();
    // ReportWithRepeatingHeaderPerPage prints a hundred lines after a page break.
    await Assert.That(Pdf.CountPages(pdf)).IsGreaterThan(1);
  }

  [Test]
  public async Task Converts_html_through_the_client()
  {
    var pdf = await ConvertHtmlAsync();

    await Assert.That(Pdf.IsComplete(pdf)).IsTrue();
    await Assert.That(Pdf.CountPages(pdf)).IsEqualTo(1);
  }

  [Test]
  public async Task Starts_no_browser_of_its_own()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Reads the process tree with ps.");
      return;
    }

    // Both endpoints convert first, so a browser the app started for either one would still run:
    // the engine keeps its browser for the next conversion. InProcessExampleTests checks that this
    // finds the browser of an app that does start one.
    await RenderReportAsync();
    await ConvertHtmlAsync();

    var processes = await ProcessTree.GetDescendantsAsync(
      appHost.GetProcessId(ReportsAppHost.RemoteExample),
      TestToken
    );

    await Assert
      .That(processes.Where(process => ProcessTree.IsBrowser(process.Name)))
      .IsEmpty()
      .Because($"the app's processes are {string.Join(", ", processes)}");
  }

  private async Task<byte[]> RenderReportAsync()
  {
    await appHost.WaitForHealthyAsync(ReportsAppHost.RemoteExample, TestToken);
    using var client = appHost.CreateHttpClient(ReportsAppHost.RemoteExample);

    using var response = await client.PostAsync(
      "/reports/reportwithrepeatingheaderperpage",
      content: null,
      TestToken
    );
    return await Responses.ReadPdfAsync(response, TestToken);
  }

  private async Task<byte[]> ConvertHtmlAsync()
  {
    await appHost.WaitForHealthyAsync(ReportsAppHost.RemoteExample, TestToken);
    using var client = appHost.CreateHttpClient(ReportsAppHost.RemoteExample);

    using var response = await client.PostAsJsonAsync(
      "/html-to-pdf",
      new { html = "<!DOCTYPE html><h1>Converted through Atli.Reports.Client</h1>" },
      TestToken
    );
    return await Responses.ReadPdfAsync(response, TestToken);
  }
}
