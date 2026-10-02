using Atli.Reports.Blazor.Models;
using Atli.Reports.Engine;
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
  /// Gets a blazor report by name
  /// </summary>
  /// <param name="name"> The name of the report to get </param>
  /// <returns> The blazor report </returns>
  BlazorReport? GetReportByName(string name);
}
