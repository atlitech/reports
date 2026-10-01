using System.IO.Pipelines;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Services;

#pragma warning disable CS0618 // Covers the obsolete PipeWriter overloads kept for BlazorReports callers.

/// <summary>
/// The obsolete PipeWriter overloads fold every <see cref="ConversionErrorKind"/> into the three problem
/// types BlazorReports returned.
/// </summary>
public class LegacyResultTests
{
  [Test]
  [Arguments(ConversionErrorKind.Busy, 1)]
  [Arguments(ConversionErrorKind.Canceled, 2)]
  [Arguments(ConversionErrorKind.BrowserUnavailable, 3)]
  [Arguments(ConversionErrorKind.SignalTimeout, 3)]
  [Arguments(ConversionErrorKind.RenderFailed, 3)]
  public async Task Conversion_errors_map_onto_the_legacy_problems(
    ConversionErrorKind kind,
    int expectedIndex
  )
  {
    await using var services = TestEngine.CreateServices(configureServices: collection =>
      collection.AddSingleton<IHtmlToPdfConverter>(FakeHtmlToPdfConverter.Failing(kind))
    );
    var report = services.GetRequiredService<BlazorReportRegistry>().AddReport<StaticReport>();
    Pipe pipe = new();

    var result = await services
      .GetRequiredService<IReportService>()
      .GenerateReport(pipe.Writer, report, TestContext.Current!.Execution.CancellationToken);

    await Assert.That(result.Index).IsEqualTo(expectedIndex);
  }
}
