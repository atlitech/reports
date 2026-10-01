using System.IO.Pipelines;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services.BrowserServices.Problems;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Services.BrowserServices;

/// <summary>
/// Service for interacting with the browser
/// </summary>
public interface IBrowserService
{
  /// <summary>
  /// Generates a report using the specified HTML
  /// </summary>
  /// <param name="pipeWriter"> The pipe writer to write the report to </param>
  /// <param name="html"> The HTML to use in the report </param>
  /// <param name="pageSettings"> The page settings to use in the report </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <returns> The result of the operation </returns>
  ValueTask<
    OneOf<Success, ServerBusyProblem, OperationCancelledProblem, BrowserProblem>
  > GenerateReport(
    PipeWriter pipeWriter,
    string html,
    BlazorReportsPageSettings pageSettings,
    CancellationToken cancellationToken
  );
}
