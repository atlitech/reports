using System.IO.Pipelines;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services.BrowserServices.Problems;
using Atli.Reports.Engine;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Services.BrowserServices;

#pragma warning disable CS0618 // Implements the obsolete IBrowserService on top of the engine.

/// <summary>
/// Implements the obsolete <see cref="IBrowserService"/> with <see cref="IHtmlToPdfConverter"/>.
/// </summary>
internal sealed class EngineBrowserService(IHtmlToPdfConverter converter) : IBrowserService
{
  public async ValueTask<
    OneOf<Success, ServerBusyProblem, OperationCancelledProblem, BrowserProblem>
  > GenerateReport(
    PipeWriter pipeWriter,
    string html,
    BlazorReportsPageSettings pageSettings,
    CancellationToken cancellationToken
  )
  {
    ArgumentNullException.ThrowIfNull(pipeWriter);
    ArgumentNullException.ThrowIfNull(pageSettings);

    var result = await converter.ConvertAsync(
      html,
      pipeWriter.AsStream(leaveOpen: true),
      PdfOptionsMapper.Map(pageSettings),
      cancellationToken
    );
    return await LegacyResults.ToLegacyResultAsync(pipeWriter, result, completeOnSuccess: true);
  }
}
