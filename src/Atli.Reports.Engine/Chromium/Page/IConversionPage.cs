namespace Atli.Reports.Engine.Chromium.Page;

/// <summary>
/// A browser page dedicated to one conversion. Disposing it discards the page and everything it
/// stored.
/// </summary>
internal interface IConversionPage : IAsyncDisposable
{
  /// <summary>
  /// Exposes <c>window.<paramref name="signalName"/>()</c> to every document the page loads from
  /// now on, including the one already loaded.
  /// </summary>
  Task EnableSignalAsync(string signalName, CancellationToken cancellationToken);

  /// <summary>
  /// Replaces the page's document with <paramref name="html"/>.
  /// </summary>
  Task SetContentAsync(string html, CancellationToken cancellationToken);

  /// <summary>
  /// Waits until the page calls the signal enabled with <see cref="EnableSignalAsync"/>.
  /// </summary>
  /// <returns><see langword="false"/> when <paramref name="timeout"/> elapsed first.</returns>
  Task<bool> WaitForSignalAsync(TimeSpan timeout, CancellationToken cancellationToken);

  /// <summary>
  /// Waits until the document has fired its <c>load</c> event and its fonts are ready.
  /// </summary>
  Task WaitForLoadAsync(CancellationToken cancellationToken);

  /// <summary>
  /// Prints the page and writes the PDF to <paramref name="destination"/>. Nothing is written
  /// unless the browser printed the page successfully.
  /// </summary>
  /// <returns>The number of PDF bytes written to <paramref name="destination"/>.</returns>
  /// <exception cref="DestinationWriteException"><paramref name="destination"/> threw.</exception>
  Task<long> PrintToPdfAsync(
    PdfOptions options,
    Stream destination,
    CancellationToken cancellationToken
  );
}

/// <summary>
/// Writing the PDF to the caller's destination stream failed. The inner exception is the
/// destination's own and is rethrown to the caller unchanged.
/// </summary>
internal sealed class DestinationWriteException : Exception
{
  public DestinationWriteException() { }

  public DestinationWriteException(string message)
    : base(message) { }

  public DestinationWriteException(string message, Exception innerException)
    : base(message, innerException) { }

  public DestinationWriteException(Exception innerException)
    : base("Writing the PDF to the destination stream failed.", innerException) { }
}
