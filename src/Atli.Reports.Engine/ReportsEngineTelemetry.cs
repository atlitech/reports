namespace Atli.Reports.Engine;

/// <summary>
/// The names under which the reports engine publishes its traces and metrics. Pass them to
/// OpenTelemetry (or to an <see cref="System.Diagnostics.ActivityListener"/> or
/// <see cref="System.Diagnostics.Metrics.MeterListener"/>) to collect the engine's telemetry.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddOpenTelemetry()
///   .WithTracing(tracing => tracing.AddSource(ReportsEngineTelemetry.ActivitySourceName))
///   .WithMetrics(metrics => metrics.AddMeter(ReportsEngineTelemetry.MeterName));
/// </code>
/// </example>
/// <remarks>
/// <para>
/// Every conversion is an <c>atli.reports.convert</c> span with a child span per stage
/// (<c>atli.reports.queue.wait</c>, <c>atli.reports.page.open</c>, <c>atli.reports.browser.launch</c>,
/// <c>atli.reports.page.set_content</c>, <c>atli.reports.page.wait</c>, <c>atli.reports.pdf.print</c>,
/// and <c>atli.reports.pdf.stream</c>). A failed conversion has the error status and an
/// <c>error.type</c> attribute holding its <see cref="ConversionErrorKind"/>. Spans and metrics never
/// contain the HTML being converted. The repository's <c>docs/engine/architecture.md</c> lists every
/// span, attribute, and instrument.
/// </para>
/// <para>
/// Without a listener for <see cref="ActivitySourceName"/>, the engine creates no spans at all.
/// </para>
/// </remarks>
public static class ReportsEngineTelemetry
{
  /// <summary>
  /// The name of the engine's <see cref="System.Diagnostics.ActivitySource"/>:
  /// <c>Atli.Reports.Engine</c>.
  /// </summary>
  public const string ActivitySourceName = "Atli.Reports.Engine";

  /// <summary>
  /// The name of the engine's <see cref="System.Diagnostics.Metrics.Meter"/>:
  /// <c>Atli.Reports.Engine</c>.
  /// </summary>
  public const string MeterName = "Atli.Reports.Engine";
}
