using OneOf;
using OneOf.Types;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// A converter whose streaming overload runs <paramref name="write"/>; a non-null result is the error.
/// </summary>
internal sealed class FakeConverter(
  Func<Stream, CancellationToken, Task<ConversionError?>> write,
  Action<PdfOptions?>? inspect = null
) : IHtmlToPdfConverter
{
  public int Calls { get; private set; }

  public ValueTask<OneOf<Stream, ConversionError>> ConvertAsync(
    string html,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  ) => throw new NotSupportedException("The server streams.");

  public async ValueTask<OneOf<Success, ConversionError>> ConvertAsync(
    string html,
    Stream destination,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  )
  {
    Calls++;
    inspect?.Invoke(options);
    var error = await write(destination, cancellationToken);
    return error is null ? new Success() : error;
  }
}
