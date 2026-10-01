using System.Diagnostics;
using Atli.Reports.Engine;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor;

/// <summary>
/// The name under which Atli Reports Blazor publishes its traces. Add it next to the engine's
/// (<see cref="ReportsEngineTelemetry.ActivitySourceName"/>) to see report generation end to end.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
///   .AddSource(BlazorReportsTelemetry.ActivitySourceName)
///   .AddSource(ReportsEngineTelemetry.ActivitySourceName));
/// </code>
/// </example>
/// <remarks>
/// Each report is an <c>atli.reports.blazor.generate</c> span with an
/// <c>atli.reports.blazor.render</c> child for rendering the component to HTML and, for PDF output,
/// the engine's <c>atli.reports.convert</c> span. Attributes: <c>atli.reports.blazor.report</c> (the
/// report's registered name, when it has one), <c>atli.reports.blazor.component</c> (the component
/// type), and <c>atli.reports.blazor.output_format</c> (<c>pdf</c> or <c>html</c>). A failed report has
/// the error status and an <c>error.type</c> attribute: the <see cref="ConversionErrorKind"/>, or the
/// exception type when rendering throws. Spans never contain the report's data or HTML.
/// </remarks>
public static class BlazorReportsTelemetry
{
  /// <summary>
  /// The name of the <see cref="ActivitySource"/>: <c>Atli.Reports.Blazor</c>.
  /// </summary>
  public const string ActivitySourceName = "Atli.Reports.Blazor";

  internal static readonly ActivitySource Source = new(
    ActivitySourceName,
    typeof(BlazorReportsTelemetry).Assembly.GetName().Version?.ToString(3)
  );

  internal static Activity? StartGenerate(
    Type component,
    string? reportName,
    Models.ReportOutputFormat outputFormat
  )
  {
    var activity = Source.StartActivity("atli.reports.blazor.generate");
    if (activity is { IsAllDataRequested: true })
    {
      if (reportName is not null)
      {
        activity.SetTag("atli.reports.blazor.report", reportName);
      }

      activity.SetTag("atli.reports.blazor.component", component.FullName);
      activity.SetTag(
        "atli.reports.blazor.output_format",
        outputFormat == Models.ReportOutputFormat.Html ? "html" : "pdf"
      );
    }

    return activity;
  }

  internal static Activity? StartRender() => Source.StartActivity("atli.reports.blazor.render");

  internal static OneOf<Success, ConversionError> Finished(
    Activity? activity,
    OneOf<Success, ConversionError> result
  )
  {
    if (activity is not null && result.TryPickT1(out var error, out _))
    {
      activity.SetTag("error.type", error.Kind.ToString());
      activity.SetStatus(ActivityStatusCode.Error, error.Message);
    }

    return result;
  }

  internal static void Failed(Activity? activity, Exception exception)
  {
    if (activity is null)
    {
      return;
    }

    activity.SetTag("error.type", exception.GetType().FullName);
    activity.SetStatus(ActivityStatusCode.Error, exception.Message);
  }
}
