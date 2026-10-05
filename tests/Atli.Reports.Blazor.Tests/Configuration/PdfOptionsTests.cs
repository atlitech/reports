using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Configuration;

public class PdfOptionsTests
{
  [Test]
  public async Task A_report_override_does_not_change_global_options_or_other_reports()
  {
    PdfOptions defaults = new()
    {
      Scale = 0.8,
      PaperSize = PaperSize.A4,
      Margins = new Margins { Top = 0.7 },
      DisplayHeaderFooter = true,
      HeaderTemplate = "<span>Shared header</span>",
    };
    var converter = FakeHtmlToPdfConverter.Succeeding();
    await using var server = await TestReportServer.StartAsync(
      app =>
      {
        app.MapBlazorReport<StaticReport>(options => options.ReportName = "before");
        app.MapBlazorReport<StaticReport>(options =>
        {
          options.ReportName = "custom";
          options.PdfOptions.Scale = 0.5;
          options.PdfOptions.PaperSize = PaperSize.Letter;
          options.PdfOptions.Margins = options.PdfOptions.Margins with { Top = 1 };
        });
        app.MapBlazorReport<StaticReport>(options => options.ReportName = "after");
        app.RegisterBlazorReport<StaticReport>(options => options.ReportName = "registered");
      },
      options => options.PdfOptions = defaults,
      services => services.AddSingleton<IHtmlToPdfConverter>(converter)
    );
    var token = TestContext.Current!.Execution.CancellationToken;

    using var custom = await server.Client.PostAsync("/custom", null, token);
    custom.EnsureSuccessStatusCode();
    await Assert.That(converter.LastOptions!.Scale).IsEqualTo(0.5);
    await Assert.That(converter.LastOptions.PaperSize).IsEqualTo(PaperSize.Letter);
    await Assert.That(converter.LastOptions.Margins.Top).IsEqualTo(1);
    await Assert.That(converter.LastOptions.HeaderTemplate).IsEqualTo(defaults.HeaderTemplate);

    foreach (var name in new[] { "before", "after" })
    {
      using var response = await server.Client.PostAsync($"/{name}", null, token);
      response.EnsureSuccessStatusCode();
      await Assert.That(converter.LastOptions!.Scale).IsEqualTo(0.8);
      await Assert.That(converter.LastOptions.PaperSize).IsEqualTo(PaperSize.A4);
      await Assert.That(converter.LastOptions.Margins.Top).IsEqualTo(0.7);
      await Assert.That(converter.LastOptions.DisplayHeaderFooter).IsTrue();
    }

    var reportService = server.Services.GetRequiredService<IReportService>();
    var registered = reportService.GetReportByName("registered")!;
    var result = await reportService.GenerateReport(Stream.Null, registered, token);
    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(converter.LastOptions!.Scale).IsEqualTo(0.8);
    await Assert.That(converter.LastOptions.PaperSize).IsEqualTo(PaperSize.A4);

    await Assert.That(defaults.Scale).IsEqualTo(0.8);
    await Assert.That(defaults.PaperSize).IsEqualTo(PaperSize.A4);
    await Assert.That(defaults.Margins.Top).IsEqualTo(0.7);
  }

  [Test]
  public async Task Direct_registration_with_only_a_name_inherits_a_snapshot_of_the_defaults()
  {
    PdfOptions defaults = new() { Scale = 0.8, PaperSize = PaperSize.A4 };
    var converter = FakeHtmlToPdfConverter.Succeeding();
    await using var services = TestEngine.CreateServices(
      options => options.PdfOptions = defaults,
      collection => collection.AddSingleton<IHtmlToPdfConverter>(converter)
    );
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    BlazorReportRegistrationOptions? capturedOptions = null;
    var report = registry.AddReport<StaticReport>(options =>
    {
      options.ReportName = "custom";
      capturedOptions = options;
    });
    capturedOptions!.PdfOptions.Scale = 0.2;
    defaults.Scale = 0.3;
    registry.DefaultPdfOptions.Scale = 0.4;

    var result = await services
      .GetRequiredService<IReportService>()
      .GenerateReport(Stream.Null, report, TestContext.Current!.Execution.CancellationToken);

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(report.Name).IsEqualTo("custom");
    await Assert.That(converter.LastOptions!.Scale).IsEqualTo(0.8);
    await Assert.That(converter.LastOptions.PaperSize).IsEqualTo(PaperSize.A4);
    await Assert.That(report.PdfOptions).IsNotSameReferenceAs(registry.DefaultPdfOptions);
  }

  [Test]
  public async Task Registration_snapshots_explicit_options_reused_by_the_caller()
  {
    PdfOptions overrides = new() { Scale = 0.6 };
    await using var services = TestEngine.CreateServices();
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    var first = registry.AddReport<StaticReport>(options =>
    {
      options.ReportName = "first";
      options.PdfOptions = overrides;
    });
    var second = registry.AddReport<StaticReport>(options =>
    {
      options.ReportName = "second";
      options.PdfOptions = overrides;
    });
    overrides.Scale = 0.9;
    first.PdfOptions.Scale = 0.5;

    await Assert.That(second.PdfOptions.Scale).IsEqualTo(0.6);
    await Assert.That(first.PdfOptions).IsNotSameReferenceAs(second.PdfOptions);
    await Assert.That(second.PdfOptions).IsNotSameReferenceAs(overrides);
  }

  [Test]
  public async Task Report_signal_overrides_do_not_mutate_shared_options_or_other_reports()
  {
    PdfOptions defaults = new()
    {
      WaitForSignal = "customReady",
      WaitTimeout = TimeSpan.FromSeconds(7),
      Scale = 0.8,
    };
    var converter = FakeHtmlToPdfConverter.Succeeding();
    await using var services = TestEngine.CreateServices(
      options => options.PdfOptions = defaults,
      collection => collection.AddSingleton<IHtmlToPdfConverter>(converter)
    );
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    var waiting = registry.AddReport<StaticReport>(options =>
    {
      options.ReportName = "waiting";
      options.PdfOptions.WaitForSignal = "reportReady";
      options.PdfOptions.WaitTimeout = TimeSpan.FromSeconds(12);
    });
    var native = registry.AddReport<StaticReport>();
    var reportService = services.GetRequiredService<IReportService>();
    var token = TestContext.Current!.Execution.CancellationToken;

    var waitingResult = await reportService.GenerateReport(Stream.Null, waiting, token);
    await Assert.That(waitingResult.IsT0).IsTrue();
    var waitingOptions = converter.LastOptions!;
    await Assert.That(waitingOptions.WaitForSignal).IsEqualTo("reportReady");
    await Assert.That(waitingOptions.WaitTimeout).IsEqualTo(TimeSpan.FromSeconds(12));
    await Assert.That(waitingOptions.Scale).IsEqualTo(0.8);
    await Assert.That(converter.LastHtml).Contains("window[\"reportReady\"]");

    var nativeResult = await reportService.GenerateReport(Stream.Null, native, token);
    await Assert.That(nativeResult.IsT0).IsTrue();
    await Assert.That(converter.LastOptions!.WaitForSignal).IsEqualTo("customReady");
    await Assert.That(converter.LastOptions.WaitTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
    await Assert.That(converter.LastHtml).Contains("window[\"customReady\"]");
    await Assert.That(defaults.WaitForSignal).IsEqualTo("customReady");
    await Assert.That(defaults.WaitTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
  }
}
