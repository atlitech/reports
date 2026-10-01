using System.IO.Pipelines;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services.BrowserServices.Problems;
using Atli.Reports.Engine;
using Microsoft.AspNetCore.Components;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Services;

/// <summary>
/// Renders Blazor components to PDF or HTML reports.
/// </summary>
/// <remarks>
/// PDF reports are converted by <see cref="IHtmlToPdfConverter"/> from Atli.Reports.Engine. Failures are
/// returned as a <see cref="ConversionError"/> instead of being thrown; exceptions thrown while rendering
/// the component, or by the destination stream, propagate to the caller.
/// </remarks>
public interface IReportService
{
  /// <summary>
  /// Renders the component <typeparamref name="T"/> with <paramref name="data"/> to PDF and writes it to
  /// <paramref name="destination"/>.
  /// </summary>
  /// <param name="destination">
  /// The writable stream that receives the PDF. It is neither flushed nor disposed. When an error is
  /// returned it may already contain part of the PDF.
  /// </param>
  /// <param name="data"> The data to pass to the component's <c>Data</c> parameter </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <typeparam name="T"> The component to render. It must declare <c>BaseStyles</c>, <c>Data</c>, and <c>GlobalAssets</c> parameters. </typeparam>
  /// <typeparam name="TD"> The type of data to use in the report </typeparam>
  /// <returns> <see cref="Success"/>, or the <see cref="ConversionError"/> that stopped the conversion </returns>
  ValueTask<OneOf<Success, ConversionError>> GenerateReport<T, TD>(
    Stream destination,
    TD data,
    CancellationToken cancellationToken = default
  )
    where T : ComponentBase
    where TD : class;

  /// <summary>
  /// Renders a registered report with <paramref name="data"/> and writes it to <paramref name="destination"/>
  /// in the report's <see cref="BlazorReport.OutputFormat"/>.
  /// </summary>
  /// <param name="destination">
  /// The writable stream that receives the report. It is neither flushed nor disposed. When an error is
  /// returned it may already contain part of the report.
  /// </param>
  /// <param name="blazorReport"> The report to generate </param>
  /// <param name="data"> The data to pass to the report component, or <see langword="null"/> for none </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <typeparam name="T"> The type of data to use in the report </typeparam>
  /// <returns> <see cref="Success"/>, or the <see cref="ConversionError"/> that stopped the report </returns>
  ValueTask<OneOf<Success, ConversionError>> GenerateReport<T>(
    Stream destination,
    BlazorReport blazorReport,
    T? data,
    CancellationToken cancellationToken = default
  )
    where T : class;

  /// <summary>
  /// Renders a registered report without data and writes it to <paramref name="destination"/> in the
  /// report's <see cref="BlazorReport.OutputFormat"/>.
  /// </summary>
  /// <param name="destination">
  /// The writable stream that receives the report. It is neither flushed nor disposed. When an error is
  /// returned it may already contain part of the report.
  /// </param>
  /// <param name="blazorReport"> The report to generate </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <returns> <see cref="Success"/>, or the <see cref="ConversionError"/> that stopped the report </returns>
  ValueTask<OneOf<Success, ConversionError>> GenerateReport(
    Stream destination,
    BlazorReport blazorReport,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Generates a report using the specified component and data
  /// </summary>
  /// <param name="pipeWriter"> The pipe writer to write the report to. It is completed after a successful PDF. </param>
  /// <param name="data"> The data to use in the report </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <typeparam name="T"> The component to use in the report </typeparam>
  /// <typeparam name="TD"> The type of data to use in the report </typeparam>
  /// <returns> The generated report </returns>
  [Obsolete(ObsoleteMessages.PipeWriterOverloads)]
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
  /// <param name="pipeWriter"> The pipe writer to write the report to. It is completed after a successful PDF. </param>
  /// <param name="blazorReport"> The report to generate </param>
  /// <param name="data"> The data to use in the report </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <typeparam name="T"> The type of data to use in the report </typeparam>
  /// <returns> The generated report </returns>
  [Obsolete(ObsoleteMessages.PipeWriterOverloads)]
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
  /// <param name="pipeWriter"> The pipe writer to write the report to. It is completed after a successful PDF. </param>
  /// <param name="blazorReport"> The report to generate </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <returns> The generated report </returns>
  [Obsolete(ObsoleteMessages.PipeWriterOverloads)]
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
