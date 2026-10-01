using System.Net.Http.Json;
using Atli.Reports.AppHost.Tests.Support;

namespace Atli.Reports.AppHost.Tests;

/// <summary>
/// examples/SimpleReportServer and examples/TailwindReportServer run the engine in their own process,
/// with the browser installed on this machine.
/// </summary>
[ClassDataSource<ReportsAppHost>(Shared = SharedType.PerTestSession)]
public class InProcessExampleTests(ReportsAppHost appHost)
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task The_simple_example_renders_a_report_with_its_own_browser()
  {
    var pdf = await PostReportAsync(
      ReportsAppHost.SimpleExample,
      "/reports/helloreport",
      JsonContent.Create(new { name = "end-to-end test" })
    );

    await Assert.That(Pdf.IsComplete(pdf)).IsTrue();

    if (!OperatingSystem.IsWindows())
    {
      // The control for RemoteExampleTests.Starts_no_browser_of_its_own: the process tree does show
      // the browser of an app that starts one.
      var processes = await ProcessTree.GetDescendantsAsync(
        appHost.GetProcessId(ReportsAppHost.SimpleExample),
        TestToken
      );
      await Assert
        .That(processes.Any(process => ProcessTree.IsBrowser(process.Name)))
        .IsTrue()
        .Because($"the app's processes are {string.Join(", ", processes)}");
    }
  }

  [Test]
  public async Task The_tailwind_example_renders_a_report_styled_by_the_tailwind_build()
  {
    // The example starts once the tailwind-css resource (bun install, then the Tailwind CLI) has
    // written the stylesheet it inlines.
    var pdf = await PostReportAsync(
      ReportsAppHost.TailwindExample,
      "/reports/reportwithtailwind",
      content: null
    );

    await Assert.That(Pdf.IsComplete(pdf)).IsTrue();
  }

  private async Task<byte[]> PostReportAsync(string resourceName, string path, HttpContent? content)
  {
    await appHost.WaitForHealthyAsync(resourceName, TestToken);
    using var client = appHost.CreateHttpClient(resourceName);

    using var response = await client.PostAsync(path, content, TestToken);
    return await Responses.ReadPdfAsync(response, TestToken);
  }
}
