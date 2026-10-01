using System.IO.Pipelines;
using Atli.Reports.Blazor.Services.BrowserServices.Problems;
using Atli.Reports.Engine;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Services;

#pragma warning disable CS0618 // Translates engine results into the obsolete problem types.

/// <summary>
/// Translates engine results into the result shape of the obsolete PipeWriter APIs.
/// </summary>
internal static class LegacyResults
{
  public static async ValueTask<
    OneOf<Success, ServerBusyProblem, OperationCancelledProblem, BrowserProblem>
  > ToLegacyResultAsync(
    PipeWriter pipeWriter,
    OneOf<Success, ConversionError> result,
    bool completeOnSuccess
  )
  {
    if (result.TryPickT1(out var error, out var success))
    {
      return error.Kind switch
      {
        ConversionErrorKind.Busy => new ServerBusyProblem(),
        ConversionErrorKind.Canceled => new OperationCancelledProblem(),
        _ => new BrowserProblem(),
      };
    }

    if (completeOnSuccess)
    {
      // BlazorReports completed the writer after a PDF, which callers reading from a Pipe rely on.
      await pipeWriter.CompleteAsync();
    }

    return success;
  }
}
