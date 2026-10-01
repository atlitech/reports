namespace Atli.Reports.Blazor.Services.BrowserServices.Problems;

/// <summary>
/// Represents a problem with the connection to the browser.
/// </summary>
/// <remarks>Nothing returns this type any more; Atli.Reports.Engine owns the browser connection.</remarks>
[Obsolete(ObsoleteMessages.LegacyProblems)]
public readonly record struct ConnectionProblem;
