using Atli.Reports.Engine;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Tests.Support;

/// <summary>
/// An <see cref="IHtmlToPdfConverter"/> that records what it was asked to convert and answers with a
/// fixed outcome, so endpoint behavior can be tested without a browser.
/// </summary>
internal sealed class FakeHtmlToPdfConverter : IHtmlToPdfConverter
{
  private readonly ConversionError? _error;
  private readonly byte[] _written;

  private FakeHtmlToPdfConverter(ConversionError? error, byte[] written)
  {
    _error = error;
    _written = written;
  }

  public string? LastHtml { get; private set; }

  public PdfOptions? LastOptions { get; private set; }

  /// <summary>
  /// Writes a tiny fake PDF and succeeds.
  /// </summary>
  public static FakeHtmlToPdfConverter Succeeding() => new(null, "%PDF-fake"u8.ToArray());

  /// <summary>
  /// Fails with <paramref name="kind"/>, after writing <paramref name="written"/> to the destination.
  /// </summary>
  public static FakeHtmlToPdfConverter Failing(ConversionErrorKind kind, byte[]? written = null) =>
    new(new ConversionError(kind, $"Fake {kind} failure"), written ?? []);

  public async ValueTask<OneOf<Stream, ConversionError>> ConvertAsync(
    string html,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  )
  {
    MemoryStream destination = new();
    var result = await ConvertAsync(html, destination, options, cancellationToken);
    destination.Position = 0;
    return result.Match<OneOf<Stream, ConversionError>>(_ => destination, error => error);
  }

  public async ValueTask<OneOf<Success, ConversionError>> ConvertAsync(
    string html,
    Stream destination,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  )
  {
    LastHtml = html;
    LastOptions = options;

    if (_written.Length > 0)
    {
      await destination.WriteAsync(_written, cancellationToken);
      await destination.FlushAsync(cancellationToken);
    }

    return _error is null ? new Success() : _error;
  }
}
