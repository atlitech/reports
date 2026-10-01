using System.Net.Http.Json;
using Atli.Reports.AppHost.Tests.Support;

namespace Atli.Reports.AppHost.Tests;

/// <summary>
/// The reports server container, built from this checkout's Dockerfile, converts HTML to PDF.
/// </summary>
[ClassDataSource<ReportsAppHost>(Shared = SharedType.PerTestSession)]
public class ReportsServerTests(ReportsAppHost appHost)
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task The_server_converts_html_to_a_pdf()
  {
    await appHost.WaitForHealthyAsync(ReportsAppHost.ReportsServer, TestToken);
    using var client = appHost.CreateHttpClient(ReportsAppHost.ReportsServer);

    using var response = await client.PostAsJsonAsync(
      "/convert",
      new
      {
        html = "<!DOCTYPE html><h1>Hello from the end-to-end test</h1>",
        options = new { paperSize = "a4" },
      },
      TestToken
    );
    var pdf = await Responses.ReadPdfAsync(response, TestToken);

    await Assert.That(Pdf.IsComplete(pdf)).IsTrue();
  }
}
