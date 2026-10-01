namespace Atli.Reports.Blazor;

/// <summary>
/// Messages for APIs kept so code written against BlazorReports keeps compiling.
/// </summary>
internal static class ObsoleteMessages
{
  public const string PipeWriterOverloads =
    "Use the overload that takes a Stream. It returns OneOf<Success, ConversionError>, whose "
    + "ConversionError.Kind says why generation failed; this overload folds every failure into "
    + "ServerBusyProblem, OperationCancelledProblem, or BrowserProblem.";

  public const string LegacyProblems =
    "Only the obsolete PipeWriter overloads of IReportService return this type. Use the overloads "
    + "that take a Stream, which return Atli.Reports.Engine.ConversionError.";

  public const string BrowserService =
    "Use Atli.Reports.Engine.IHtmlToPdfConverter, which AddBlazorReports registers. It reports "
    + "why a conversion failed through ConversionError.";

  public const string BrowserPoolSize =
    "Has no effect. Atli.Reports.Engine owns the browser and its pages; configure it with "
    + "services.AddReportsEngine(...).";
}
