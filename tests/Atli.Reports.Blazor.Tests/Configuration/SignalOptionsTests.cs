using System.Net;
using System.Text;
using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Configuration;

/// <summary>
/// How the JavaScript completion settings reach the template and the engine, without a browser.
/// </summary>
public class SignalOptionsTests
{
  [Test]
  public async Task Global_settings_apply_to_mapped_reports_and_reports_can_override_them()
  {
    var converter = FakeHtmlToPdfConverter.Succeeding();
    await using var server = await TestReportServer.StartAsync(
      app =>
      {
        app.MapBlazorReport<DelayedScriptReport>();
        app.MapBlazorReport<StaticReport>(options => options.PdfOptions.WaitForSignal = null);
      },
      options =>
      {
        options.PdfOptions.WaitForSignal = "reportReady";
        options.PdfOptions.WaitTimeout = TimeSpan.FromSeconds(12);
      },
      services => services.AddSingleton<IHtmlToPdfConverter>(converter)
    );
    var cancellationToken = TestContext.Current!.Execution.CancellationToken;

    using var waiting = await server.Client.PostAsync(
      "/delayedscriptreport",
      null,
      cancellationToken
    );
    var waitingOptions = converter.LastOptions!;
    using var notWaiting = await server.Client.PostAsync("/staticreport", null, cancellationToken);
    var notWaitingOptions = converter.LastOptions!;

    await Assert.That(waiting.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(waitingOptions.WaitForSignal).IsEqualTo("reportReady");
    await Assert.That(waitingOptions.WaitTimeout).IsEqualTo(TimeSpan.FromSeconds(12));
    await Assert.That(notWaiting.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(notWaitingOptions.WaitForSignal).IsNull();
  }

  [Test]
  public async Task The_template_defines_blazorReport_only_for_reports_that_wait()
  {
    await using var services = TestEngine.CreateServices();
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    var waiting = registry.AddReport<DelayedScriptReport>(options =>
    {
      options.OutputFormat = ReportOutputFormat.Html;
      options.PdfOptions.WaitForSignal = "reportReady";
    });
    var notWaiting = registry.AddReport<StaticReport>(options =>
      options.OutputFormat = ReportOutputFormat.Html
    );

    var waitingHtml = await RenderHtmlAsync(services, waiting);
    var notWaitingHtml = await RenderHtmlAsync(services, notWaiting);

    await Assert
      .That(waitingHtml)
      .Contains("window.blazorReport={completed:function(){var signal=window[\"reportReady\"];");
    await Assert.That(waitingHtml).DoesNotContain("suppress-error");
    await Assert.That(notWaitingHtml).DoesNotContain("blazorReport");
  }

  [Test]
  [Arguments(-2)]
  [Arguments(-1000)]
  public async Task Registration_rejects_a_negative_timeout(int milliseconds)
  {
    await using var services = TestEngine.CreateServices();
    var registry = services.GetRequiredService<BlazorReportRegistry>();

    await Assert
      .That(() =>
        registry.AddReport<StaticReport>(options =>
        {
          options.PdfOptions.WaitForSignal = "reportReady";
          options.PdfOptions.WaitTimeout = TimeSpan.FromMilliseconds(milliseconds);
        })
      )
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  [Arguments(-1)]
  [Arguments(0)]
  [Arguments(12000)]
  public async Task Registration_accepts_infinite_zero_and_positive_timeouts(int milliseconds)
  {
    await using var services = TestEngine.CreateServices();
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    var report = registry.AddReport<StaticReport>(options =>
    {
      options.PdfOptions.WaitForSignal = "reportReady";
      options.PdfOptions.WaitTimeout = TimeSpan.FromMilliseconds(milliseconds);
    });

    await Assert
      .That(report.PdfOptions.WaitTimeout)
      .IsEqualTo(TimeSpan.FromMilliseconds(milliseconds));
  }

  [Test]
  [Arguments("")]
  [Arguments(" ")]
  public async Task Registration_rejects_a_blank_signal(string signalName)
  {
    await using var services = TestEngine.CreateServices();
    var registry = services.GetRequiredService<BlazorReportRegistry>();

    await Assert
      .That(() =>
        registry.AddReport<StaticReport>(options => options.PdfOptions.WaitForSignal = signalName)
      )
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Invalid_global_timeouts_fail_when_the_registry_is_created()
  {
    await using var services = TestEngine.CreateServices(options =>
      options.PdfOptions.WaitTimeout = TimeSpan.FromSeconds(-1)
    );

    await Assert
      .That(() => services.GetRequiredService<BlazorReportRegistry>())
      .Throws<ArgumentOutOfRangeException>();
  }

  private static async Task<string> RenderHtmlAsync(IServiceProvider services, BlazorReport report)
  {
    using MemoryStream destination = new();
    var result = await services
      .GetRequiredService<IReportService>()
      .GenerateReport(destination, report, TestContext.Current!.Execution.CancellationToken);
    await Assert.That(result.IsT0).IsTrue();
    return Encoding.UTF8.GetString(destination.ToArray());
  }
}
