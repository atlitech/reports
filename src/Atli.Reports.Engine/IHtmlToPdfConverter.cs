using OneOf;
using OneOf.Types;

namespace Atli.Reports.Engine;

/// <summary>
/// Converts HTML documents to PDF by rendering them in a Chromium-based browser.
/// </summary>
/// <remarks>
/// <para>
/// Register an implementation with
/// <see cref="ReportsEngineServiceCollectionExtensions.AddReportsEngine(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{ReportsEngineOptions}?)"/>.
/// The registered converter is a thread-safe singleton.
/// </para>
/// <para>
/// Conversion failures are returned as a <see cref="ConversionError"/> instead of being thrown;
/// inspect <see cref="ConversionError.Kind"/> to decide how to react. Canceling the
/// <see cref="CancellationToken"/> produces a <see cref="ConversionErrorKind.Canceled"/> error.
/// </para>
/// </remarks>
public interface IHtmlToPdfConverter
{
  /// <summary>
  /// Converts <paramref name="html"/> to PDF and returns the document in memory.
  /// </summary>
  /// <param name="html">The complete HTML document to render.</param>
  /// <param name="options">Page and rendering options. <see langword="null"/> uses the defaults of <see cref="PdfOptions"/>.</param>
  /// <param name="cancellationToken">Cancels the conversion.</param>
  /// <returns>
  /// A readable, seekable stream positioned at the start of the PDF, which the caller owns and must dispose;
  /// or a <see cref="ConversionError"/> describing why the conversion failed.
  /// </returns>
  /// <exception cref="ArgumentNullException"><paramref name="html"/> is <see langword="null"/>.</exception>
  ValueTask<OneOf<Stream, ConversionError>> ConvertAsync(
    string html,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Converts <paramref name="html"/> to PDF and writes the document to <paramref name="destination"/>.
  /// </summary>
  /// <param name="html">The complete HTML document to render.</param>
  /// <param name="destination">
  /// The writable stream that receives the PDF bytes. The converter writes from the stream's current
  /// position and neither flushes nor disposes it.
  /// </param>
  /// <param name="options">Page and rendering options. <see langword="null"/> uses the defaults of <see cref="PdfOptions"/>.</param>
  /// <param name="cancellationToken">Cancels the conversion.</param>
  /// <returns>
  /// <see cref="Success"/> once the whole PDF has been written; or a <see cref="ConversionError"/>
  /// describing why the conversion failed. When an error is returned, <paramref name="destination"/>
  /// may already contain part of a PDF.
  /// </returns>
  /// <exception cref="ArgumentNullException"><paramref name="html"/> or <paramref name="destination"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.</exception>
  /// <remarks>
  /// Exceptions thrown by <paramref name="destination"/> itself (for example an <see cref="IOException"/>
  /// when a network peer disconnects) propagate to the caller unchanged.
  /// </remarks>
  ValueTask<OneOf<Success, ConversionError>> ConvertAsync(
    string html,
    Stream destination,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  );
}
