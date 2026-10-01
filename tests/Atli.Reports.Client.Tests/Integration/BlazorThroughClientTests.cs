using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Client.Tests.Reports;
using Atli.Reports.Client.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Client.Tests.Integration;

/// <summary>
/// An app with Blazor reports and the client renders its components locally, converts them on the
/// server, and never starts a browser of its own.
/// </summary>
/// <remarks>
/// The app's engine is set up to give itself away: its browser executable is a script that leaves a
/// marker file and exits, and it warms that browser up when the host starts. The app could not
/// produce a PDF itself, and any attempt to start its browser leaves the marker.
/// </remarks>
[ClassDataSource<RunningReportsServer>(Shared = SharedType.PerTestSession)]
public class BlazorThroughClientTests(RunningReportsServer server)
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [NotInParallel("chrome")]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Blazor_reports_convert_on_the_server_and_the_app_starts_no_browser(
    bool blazorFirst
  )
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var browser = BrowserLaunchDetector.Create();
    using var app = await StartAppAsync(
      browser,
      builder =>
      {
        if (blazorFirst)
        {
          builder.Services.AddBlazorReports();
        }

        builder.AddReportsClient("reports");

        if (!blazorFirst)
        {
          builder.Services.AddBlazorReports();
        }
      },
      server.Endpoint
    );
    using MemoryStream destination = new();

    var result = await GenerateAsync(app, destination, new InvoiceData("INV-42"));

    await Assert.That(result.IsT0).IsTrue().Because(Describe(result.Value));
    await Assert.That(Pdf.IsComplete(destination.ToArray())).IsTrue();
    await Assert.That(browser.Launched).IsFalse();
  }

  [Test]
  public async Task Without_the_client_the_same_app_starts_its_browser_at_once()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    // The control for the test above: the detector does see the engine start a browser.
    using var browser = BrowserLaunchDetector.Create();
    using var app = await StartAppAsync(
      browser,
      builder => builder.Services.AddBlazorReports(),
      endpoint: null
    );

    await Assert.That(browser.Launched).IsTrue();

    var result = await GenerateAsync(app, Stream.Null, new InvoiceData("INV-43"));
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
  }

  private static async Task<IHost> StartAppAsync(
    BrowserLaunchDetector browser,
    Action<HostApplicationBuilder> register,
    Uri? endpoint
  )
  {
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    if (endpoint is not null)
    {
      builder.Configuration["ConnectionStrings:reports"] = $"Endpoint={endpoint}";
    }

    builder.Services.AddReportsEngine(options =>
    {
      options.Browser.ExecutablePath = browser.Path;
      options.Browser.WarmUpOnStartup = true;
    });
    register(builder);

    var app = builder.Build();
    await app.StartAsync(TestToken);
    return app;
  }

  private static async Task<OneOf<Success, ConversionError>> GenerateAsync(
    IHost app,
    Stream destination,
    InvoiceData data
  )
  {
    var report = app
      .Services.GetRequiredService<BlazorReportRegistry>()
      .AddReport<InvoiceReport, InvoiceData>();
    return await app
      .Services.GetRequiredService<IReportService>()
      .GenerateReport(destination, report, data, TestToken);
  }

  private static string Describe(object value) =>
    value is ConversionError error ? $"{error.Kind}: {error.Message}" : "succeeded";
}
