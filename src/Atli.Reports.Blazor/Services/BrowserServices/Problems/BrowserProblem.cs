namespace Atli.Reports.Blazor.Services.BrowserServices.Problems;

/// <summary>
/// Represents a report that could not be converted to PDF for any reason other than a busy engine or
/// cancellation: the browser was unavailable, the page failed to render, or a timeout elapsed.
/// </summary>
[Obsolete(ObsoleteMessages.LegacyProblems)]
public readonly record struct BrowserProblem;
