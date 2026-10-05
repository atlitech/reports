using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Services;

public class ReportServiceTests
{
  [Test]
  [Arguments(ConversionErrorKind.InvalidRequest)]
  [Arguments(ConversionErrorKind.Busy)]
  [Arguments(ConversionErrorKind.Canceled)]
  [Arguments(ConversionErrorKind.BrowserUnavailable)]
  [Arguments(ConversionErrorKind.Timeout)]
  [Arguments(ConversionErrorKind.SignalTimeout)]
  [Arguments(ConversionErrorKind.RenderFailed)]
  [Arguments(ConversionErrorKind.Unauthorized)]
  [Arguments(ConversionErrorKind.Forbidden)]
  [Arguments(ConversionErrorKind.PolicyDenied)]
  public async Task Conversion_errors_keep_their_kind_and_message(ConversionErrorKind kind)
  {
    await using var services = TestEngine.CreateServices(configureServices: collection =>
      collection.AddSingleton<IHtmlToPdfConverter>(FakeHtmlToPdfConverter.Failing(kind))
    );
    var report = services.GetRequiredService<BlazorReportRegistry>().AddReport<StaticReport>();
    using MemoryStream destination = new();

    var result = await services
      .GetRequiredService<IReportService>()
      .GenerateReport(destination, report, TestContext.Current!.Execution.CancellationToken);

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(kind);
    await Assert.That(result.AsT1.Message).IsEqualTo($"Fake {kind} failure");
    await Assert.That(destination.CanWrite).IsTrue();
  }

  [Test]
  public async Task Registered_reports_use_the_template_and_global_signal_settings()
  {
    var converter = FakeHtmlToPdfConverter.Succeeding();
    await using var services = TestEngine.CreateServices(
      options =>
      {
        options.PdfOptions.WaitForSignal = "reportReady";
        options.PdfOptions.WaitTimeout = TimeSpan.FromSeconds(12);
      },
      collection => collection.AddSingleton<IHtmlToPdfConverter>(converter)
    );
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    registry.AddReport<GreetingReport>();
    var reportService = services.GetRequiredService<IReportService>();
    var report = reportService.GetReportByName("GreetingReport")!;
    using MemoryStream destination = new();

    var result = await reportService.GenerateReport(
      destination,
      report,
      new GreetingData("registered"),
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(converter.LastHtml).StartsWith("<!DOCTYPE html>");
    await Assert.That(converter.LastHtml).Contains("<h1>Hello, registered</h1>");
    await Assert.That(converter.LastHtml).Contains("window.blazorReport=");
    await Assert.That(converter.LastOptions!.WaitForSignal).IsEqualTo("reportReady");
    await Assert.That(converter.LastOptions.WaitTimeout).IsEqualTo(TimeSpan.FromSeconds(12));
    await Assert.That(destination.CanWrite).IsTrue();
  }
}
