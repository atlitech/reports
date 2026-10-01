using System.IO.Pipelines;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services.BrowserServices.Problems;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Services.BrowserServices;

/// <summary>
/// Converts HTML to PDF.
/// </summary>
/// <remarks>
/// This is a thin adapter over <see cref="Atli.Reports.Engine.IHtmlToPdfConverter"/>, kept so code
/// written against BlazorReports keeps compiling. New code should use the engine converter directly.
/// </remarks>
[Obsolete(ObsoleteMessages.BrowserService)]
public interface IBrowserService
{
  /// <summary>
  /// Converts <paramref name="html"/> to PDF and writes it to <paramref name="pipeWriter"/>.
  /// </summary>
  /// <param name="pipeWriter"> The pipe writer to write the PDF to. It is completed once the whole PDF is written. </param>
  /// <param name="html"> The HTML to convert </param>
  /// <param name="pageSettings"> The page settings to use for the PDF </param>
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
