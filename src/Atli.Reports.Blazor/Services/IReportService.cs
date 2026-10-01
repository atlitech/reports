using System.IO.Pipelines;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services.BrowserServices.Problems;
using Microsoft.AspNetCore.Components;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Services;

/// <summary>
/// Service for generating reports
/// </summary>
public interface IReportService
{
  /// <summary>
  /// Generates a report using the specified component and data
  /// </summary>
  /// <param name="pipeWriter"> The pipe writer to write the report to </param>
  /// <param name="data"> The data to use in the report </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <typeparam name="T"> The component to use in the report </typeparam>
  /// <typeparam name="TD"> The type of data to use in the report </typeparam>
  /// <returns> The generated report </returns>
  ValueTask<
    OneOf<Success, ServerBusyProblem, OperationCancelledProblem, BrowserProblem>
  > GenerateReport<T, TD>(
    PipeWriter pipeWriter,
    TD data,
    CancellationToken cancellationToken = default
  )
    where T : ComponentBase
    where TD : class;

  /// <summary>
  /// Generates a report using the specified component and data
  /// </summary>
  /// <param name="pipeWriter"> The pipe writer to write the report to </param>
  /// <param name="blazorReport"> The report to generate </param>
  /// <param name="data"> The data to use in the report </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <typeparam name="T"> The type of data to use in the report </typeparam>
  /// <returns> The generated report </returns>
  ValueTask<
    OneOf<Success, ServerBusyProblem, OperationCancelledProblem, BrowserProblem>
  > GenerateReport<T>(
    PipeWriter pipeWriter,
    BlazorReport blazorReport,
    T? data,
    CancellationToken cancellationToken = default
  )
    where T : class;

  /// <summary>
  /// Generates a report using the specified component
  /// </summary>
  /// <param name="pipeWriter"> The pipe writer to write the report to </param>
  /// <param name="blazorReport"> The report to generate </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <returns> The generated report </returns>
  ValueTask<
    OneOf<Success, ServerBusyProblem, OperationCancelledProblem, BrowserProblem>
  > GenerateReport(
    PipeWriter pipeWriter,
    BlazorReport blazorReport,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Gets a blazor report by name
  /// </summary>
  /// <param name="name"> The name of the report to get </param>
  /// <returns> The blazor report </returns>
  BlazorReport? GetReportByName(string name);
}
