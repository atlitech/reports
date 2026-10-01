using System.Collections.Concurrent;
using System.Diagnostics;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Services;

/// <summary>
/// The spans report generation produces, with a fake converter instead of a browser.
/// </summary>
public class TracingTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task A_report_is_a_generate_span_with_a_render_child()
  {
    using var spans = new Spans();
    await using var services = TestEngine.CreateServices(configureServices: s =>
      s.AddSingleton<IHtmlToPdfConverter>(FakeHtmlToPdfConverter.Succeeding())
    );
    var report = services.GetRequiredService<BlazorReportRegistry>().AddReport<StaticReport>();
    using MemoryStream destination = new();

    var result = await services
      .GetRequiredService<IReportService>()
      .GenerateReport(destination, report, TestToken);

    await Assert.That(result.IsT0).IsTrue();
    var generate = spans.Single("atli.reports.blazor.generate");
    await Assert.That(generate.ParentSpanId).IsEqualTo(spans.Root.SpanId);
    await Assert.That(generate.GetTagItem("atli.reports.blazor.report")).IsEqualTo(report.Name);
    await Assert
      .That(generate.GetTagItem("atli.reports.blazor.component"))
      .IsEqualTo(typeof(StaticReport).FullName);
    await Assert.That(generate.GetTagItem("atli.reports.blazor.output_format")).IsEqualTo("pdf");
    await Assert.That(generate.Status).IsEqualTo(ActivityStatusCode.Unset);
    var render = spans.Single("atli.reports.blazor.render");
    await Assert.That(render.ParentSpanId).IsEqualTo(generate.SpanId);
  }

  [Test]
  public async Task A_failed_conversion_fails_the_generate_span_with_its_kind()
  {
    using var spans = new Spans();
    await using var services = TestEngine.CreateServices(configureServices: s =>
      s.AddSingleton<IHtmlToPdfConverter>(FakeHtmlToPdfConverter.Failing(ConversionErrorKind.Busy))
    );
    var report = services.GetRequiredService<BlazorReportRegistry>().AddReport<StaticReport>();
    using MemoryStream destination = new();

    var result = await services
      .GetRequiredService<IReportService>()
      .GenerateReport(destination, report, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Busy);
    var generate = spans.Single("atli.reports.blazor.generate");
    await Assert.That(generate.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(generate.StatusDescription).IsEqualTo(result.AsT1.Message);
    await Assert.That(generate.GetTagItem("error.type")).IsEqualTo("Busy");
    await Assert
      .That(spans.Single("atli.reports.blazor.render").Status)
      .IsEqualTo(ActivityStatusCode.Unset);
  }

  /// <summary>
  /// Records the Blazor spans in a trace of the test's own, so parallel tests do not interfere.
  /// </summary>
  private sealed class Spans : IDisposable
  {
    private static readonly ActivitySource TestSource = new("Atli.Reports.Blazor.Tests");

    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    public Spans()
    {
      _listener = new ActivityListener
      {
        ShouldListenTo = source =>
          source.Name == BlazorReportsTelemetry.ActivitySourceName || source == TestSource,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
          ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = activity =>
        {
          if (Root is { } root && activity.TraceId == root.TraceId)
          {
            _stopped.Enqueue(activity);
          }
        },
      };
      ActivitySource.AddActivityListener(_listener);
      Activity.Current = null;
      Root = TestSource.StartActivity("test")!;
    }

    public Activity Root { get; }

    public Activity Single(string name) => _stopped.Single(span => span.OperationName == name);

    public void Dispose()
    {
      Root.Dispose();
      _listener.Dispose();
    }
  }
}
