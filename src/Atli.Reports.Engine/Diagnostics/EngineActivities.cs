using System.Diagnostics;
using System.Globalization;

namespace Atli.Reports.Engine.Diagnostics;

/// <summary>
/// The engine's traces, published on the <c>Atli.Reports.Engine</c> activity source
/// (<see cref="ReportsEngineTelemetry.ActivitySourceName"/>).
/// </summary>
/// <remarks>
/// <para>
/// A conversion is a <see cref="Spans.Convert"/> span; each stage it reaches is a child span.
/// Without a listener, <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns
/// <see langword="null"/> and nothing here allocates. No span, attribute, or event ever contains the
/// HTML being converted.
/// </para>
/// <para>
/// The conversion span carries <c>error.type</c> (the <see cref="ConversionErrorKind"/>) and the
/// error status when the conversion fails; the stage span it failed in also gets the error status.
/// </para>
/// </remarks>
internal static class EngineActivities
{
  /// <summary>
  /// The version the engine's activity source and meter report: the assembly version, as
  /// <c>major.minor.patch</c>.
  /// </summary>
  /// <remarks>Declared before <see cref="Source"/>, whose initializer reads it.</remarks>
  public static readonly string? TelemetryVersion = typeof(EngineActivities)
    .Assembly.GetName()
    .Version?.ToString(3);

  /// <summary>
  /// The engine's activity source.
  /// </summary>
  public static readonly ActivitySource Source = new(
    new ActivitySourceOptions(ReportsEngineTelemetry.ActivitySourceName)
    {
      Version = TelemetryVersion,
    }
  );

  /// <summary>
  /// The engine span the caller runs in, if any. Engine code enriches only its own spans, never a
  /// caller's.
  /// </summary>
  public static Activity? Current =>
    Activity.Current is { } current && current.Source == Source ? current : null;

  /// <summary>
  /// Starts the span of one conversion, tagged with its non-sensitive options.
  /// </summary>
  public static Activity? StartConversion(string html, PdfOptions options)
  {
    var activity = Source.StartActivity(Spans.Convert);
    if (activity is { IsAllDataRequested: true })
    {
      activity.SetTag(Tags.HtmlLength, html.Length);
      activity.SetTag(Tags.PaperSize, DescribePaperSize(options.PaperSize));
      activity.SetTag(
        Tags.Orientation,
        options.Orientation == PageOrientation.Landscape ? "landscape" : "portrait"
      );
      activity.SetTag(Tags.WaitForSignal, options.WaitForSignal is not null);
      activity.SetTag(Tags.Tagged, options.GenerateTaggedPdf);
    }

    return activity;
  }

  /// <summary>
  /// Ends <paramref name="previous"/>, if any, and starts the next stage span.
  /// </summary>
  public static Activity? NextStage(Activity? previous, string name)
  {
    previous?.Dispose();
    return Source.StartActivity(name);
  }

  /// <summary>
  /// Marks the stage span the conversion failed in. An unexpected failure (a defect rather than a
  /// failure the engine anticipates) also records its exception.
  /// </summary>
  public static void StageFailed(Activity? stage, ConversionError error, bool unexpected = false)
  {
    if (stage is null)
    {
      return;
    }

    stage.SetStatus(ActivityStatusCode.Error, error.Message);
    if (unexpected && error.Exception is { } exception)
    {
      stage.AddException(exception);
    }
  }

  /// <summary>
  /// Marks a stage span that ended with <paramref name="exception"/>.
  /// </summary>
  public static void StageFailed(Activity? stage, Exception exception) =>
    stage?.SetStatus(ActivityStatusCode.Error, exception.Message);

  /// <summary>
  /// Records the conversion's outcome on its span: nothing for a success; <c>error.type</c> (the
  /// <see cref="ConversionErrorKind"/>) and the error status for a failure.
  /// </summary>
  public static void ConversionFinished(Activity? conversion, ConversionError? error)
  {
    if (conversion is null || error is null)
    {
      return;
    }

    conversion.SetTag(Tags.ErrorType, error.Kind.ToString());
    conversion.SetStatus(ActivityStatusCode.Error, error.Message);
  }

  /// <summary>
  /// Records an exception that escapes the operation of <paramref name="activity"/>: the fully
  /// qualified exception type as <c>error.type</c>, and the error status.
  /// </summary>
  public static void Failed(Activity? activity, Exception exception)
  {
    if (activity is null)
    {
      return;
    }

    activity.SetTag(Tags.ErrorType, exception.GetType().FullName);
    activity.SetStatus(ActivityStatusCode.Error, exception.Message);
  }

  /// <summary>
  /// Starts the span of a browser launch.
  /// </summary>
  /// <param name="generation">The browser being launched.</param>
  /// <param name="attempt">
  /// The launches in a row this one makes, counting the failed ones before it: 1 unless the previous
  /// launch failed.
  /// </param>
  public static Activity? StartBrowserLaunch(int generation, int attempt)
  {
    var activity = Source.StartActivity(Spans.BrowserLaunch);
    activity?.SetTag(Tags.BrowserGeneration, generation);
    activity?.SetTag(Tags.BrowserLaunchAttempt, attempt);
    return activity;
  }

  /// <summary>
  /// Notes on the current engine span that a browser is being recycled, and why.
  /// </summary>
  /// <param name="generation">The browser being recycled.</param>
  /// <param name="reason"><c>max_lifetime</c> or <c>max_conversions</c>.</param>
  public static void BrowserRecycling(int generation, string reason) =>
    Current?.AddEvent(
      new ActivityEvent(
        Events.BrowserRecycle,
        tags: new ActivityTagsCollection
        {
          { Tags.BrowserGeneration, generation },
          { Tags.RecycleReason, reason },
        }
      )
    );

  /// <summary>
  /// A paper size's common name (<c>letter</c>, <c>legal</c>, <c>a4</c>, <c>a3</c>), or else its
  /// dimensions in inches (<c>8x10in</c>).
  /// </summary>
  internal static string DescribePaperSize(PaperSize size)
  {
    if (size == PaperSize.Letter)
    {
      return "letter";
    }

    if (size == PaperSize.A4)
    {
      return "a4";
    }

    if (size == PaperSize.Legal)
    {
      return "legal";
    }

    if (size == PaperSize.A3)
    {
      return "a3";
    }

    return string.Create(CultureInfo.InvariantCulture, $"{size.Width}x{size.Height}in");
  }

  /// <summary>
  /// Span names.
  /// </summary>
  internal static class Spans
  {
    /// <summary>One conversion, from the call to the result, queue wait included.</summary>
    public const string Convert = "atli.reports.convert";

    /// <summary>Waiting for a turn. Only conversions that had to wait have one.</summary>
    public const string QueueWait = "atli.reports.queue.wait";

    /// <summary>Leasing the browser (launching one if needed) and opening the isolated page.</summary>
    public const string PageOpen = "atli.reports.page.open";

    /// <summary>Starting a browser process and connecting to it.</summary>
    public const string BrowserLaunch = "atli.reports.browser.launch";

    /// <summary>Registering the completion signal, if any, and setting the document content.</summary>
    public const string SetContent = "atli.reports.page.set_content";

    /// <summary>Waiting for the load event and fonts, or for the completion signal.</summary>
    public const string PageWait = "atli.reports.page.wait";

    /// <summary>Printing the PDF and streaming it to the destination.</summary>
    public const string Print = "atli.reports.pdf.print";

    /// <summary>Streaming the printed PDF out of the browser into the destination.</summary>
    public const string Stream = "atli.reports.pdf.stream";
  }

  /// <summary>
  /// Span event names.
  /// </summary>
  internal static class Events
  {
    /// <summary>A browser was retired for its age or its conversion count.</summary>
    public const string BrowserRecycle = "atli.reports.browser.recycle";
  }

  /// <summary>
  /// Attribute names.
  /// </summary>
  internal static class Tags
  {
    public const string ErrorType = "error.type";
    public const string HtmlLength = "atli.reports.html.length";
    public const string PaperSize = "atli.reports.pdf.paper_size";
    public const string Orientation = "atli.reports.pdf.orientation";
    public const string Tagged = "atli.reports.pdf.tagged";
    public const string WaitForSignal = "atli.reports.pdf.wait_for_signal";
    public const string PdfSize = "atli.reports.pdf.size";
    public const string WaitFor = "atli.reports.page.wait_for";
    public const string BrowserGeneration = "atli.reports.browser.generation";
    public const string BrowserProcessId = "atli.reports.browser.pid";
    public const string BrowserLaunchAttempt = "atli.reports.browser.launch.attempt";
    public const string RecycleReason = "atli.reports.browser.recycle.reason";
  }
}
