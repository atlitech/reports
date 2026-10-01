using Microsoft.Net.Http.Headers;

namespace Atli.Reports.Server.Endpoints;

/// <summary>
/// The response body as a write-only stream that commits the PDF response headers with the first
/// byte, so an error found before any byte was written can still become a proper error response.
/// </summary>
internal sealed class PdfResponseStream(HttpResponse response) : Stream
{
  private static readonly string ContentDisposition = new ContentDispositionHeaderValue(
    "attachment"
  )
  {
    FileName = "output.pdf",
    FileNameStar = "output.pdf",
  }.ToString();

  /// <summary>
  /// Whether any part of the PDF has been written, which commits the response to a 200.
  /// </summary>
  public bool HasStarted { get; private set; }

  public override bool CanRead => false;

  public override bool CanSeek => false;

  public override bool CanWrite => true;

  public override long Length => throw new NotSupportedException();

  public override long Position
  {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  public override async ValueTask WriteAsync(
    ReadOnlyMemory<byte> buffer,
    CancellationToken cancellationToken = default
  )
  {
    Start();
    await response.Body.WriteAsync(buffer, cancellationToken);
  }

  public override Task WriteAsync(
    byte[] buffer,
    int offset,
    int count,
    CancellationToken cancellationToken
  ) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

  public override Task FlushAsync(CancellationToken cancellationToken) =>
    HasStarted ? response.Body.FlushAsync(cancellationToken) : Task.CompletedTask;

  /// <summary>
  /// Commits the PDF headers even when nothing was written.
  /// </summary>
  public void Start()
  {
    if (HasStarted)
    {
      return;
    }

    HasStarted = true;
    response.StatusCode = StatusCodes.Status200OK;
    response.ContentType = "application/pdf";
    response.Headers.ContentDisposition = ContentDisposition;
  }

  public override void Flush() { }

  public override void Write(byte[] buffer, int offset, int count) =>
    throw new NotSupportedException("Write the response asynchronously.");

  public override int Read(byte[] buffer, int offset, int count) =>
    throw new NotSupportedException();

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  public override void SetLength(long value) => throw new NotSupportedException();
}
