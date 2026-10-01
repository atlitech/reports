namespace Atli.Reports.Engine;

/// <summary>
/// Describes why an HTML-to-PDF conversion failed.
/// </summary>
/// <param name="Kind">
/// The category of the failure. Use it to decide how to react (retry, report a client error, and so on)
/// instead of parsing <paramref name="Message"/>.
/// </param>
/// <param name="Message">
/// A human-readable description of the failure, suitable for logs. It never contains the HTML being converted.
/// </param>
/// <param name="Exception">The exception that caused the failure, if any.</param>
public sealed record ConversionError(
  ConversionErrorKind Kind,
  string Message,
  Exception? Exception = null
);
