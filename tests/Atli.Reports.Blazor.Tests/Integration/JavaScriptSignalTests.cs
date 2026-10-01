using System.Net;
using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Integration;

/// <summary>
/// Reports whose JavaScript must finish before printing, rendered in a real browser.
/// </summary>
[NotInParallel("chrome")]
public class JavaScriptSignalTests
{
  [Test]
  public async Task Waits_for_the_report_to_signal_completion_and_returns_the_pdf()
  {
    await using var server = await TestReportServer.StartAsync(app =>
      app.MapBlazorReport<DelayedScriptReport>(options =>
      {
        options.JavaScriptSettings.WaitForCompletedSignal = true;
        options.JavaScriptSettings.CompletedSignalTimeout = TestEngine.GenerousTimeout;
      })
    );

    using var response = await server.Client.PostAsync(
      "/delayedscriptreport",
      content: null,
      TestContext.Current!.Execution.CancellationToken
    );

    var body = await response.Content.ReadAsByteArrayAsync();
    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(Pdf.ReadHeader(body)).IsEqualTo(Pdf.Header);
  }

  [Test]
  public async Task A_report_that_signals_while_loading_succeeds()
  {
    await using var services = TestEngine.CreateServices();
    var report = services
      .GetRequiredService<BlazorReportRegistry>()
      .AddReport<ImmediateScriptReport>(WaitingFor(TestEngine.GenerousTimeout));
    using MemoryStream destination = new();

    var result = await services
      .GetRequiredService<IReportService>()
      .GenerateReport(destination, report, TestContext.Current!.Execution.CancellationToken);

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(Pdf.ReadHeader(destination.ToArray())).IsEqualTo(Pdf.Header);
  }

  [Test]
  public async Task A_report_that_never_signals_fails_with_SignalTimeout()
  {
    await using var services = TestEngine.CreateServices();
    var report = services
      .GetRequiredService<BlazorReportRegistry>()
      .AddReport<SilentScriptReport>(WaitingFor(TimeSpan.FromMilliseconds(500)));
    using MemoryStream destination = new();

    var result = await services
      .GetRequiredService<IReportService>()
      .GenerateReport(destination, report, TestContext.Current!.Execution.CancellationToken);

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.SignalTimeout);
    await Assert.That(destination.Length).IsEqualTo(0);
  }

  [Test]
  public async Task The_mapped_endpoint_answers_504_when_the_report_never_signals()
  {
    await using var server = await TestReportServer.StartAsync(
      app => app.MapBlazorReport<SilentScriptReport>(),
      options =>
      {
        options.JavaScriptSettings.WaitForCompletedSignal = true;
        options.JavaScriptSettings.CompletedSignalTimeout = TimeSpan.FromMilliseconds(500);
      }
    );

    using var response = await server.Client.PostAsync(
      "/silentscriptreport",
      content: null,
      TestContext.Current!.Execution.CancellationToken
    );

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.GatewayTimeout);
    await Assert
      .That(response.Content.Headers.ContentType?.MediaType)
      .IsEqualTo("application/problem+json");
  }

  private static BlazorReportRegistrationOptions WaitingFor(TimeSpan timeout) =>
    new()
    {
      JavaScriptSettings = new BlazorReportsJavaScriptSettings
      {
        WaitForCompletedSignal = true,
        CompletedSignalTimeout = timeout,
      },
    };
}
