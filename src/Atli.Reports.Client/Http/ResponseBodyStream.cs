namespace Atli.Reports.Client.Http;

/// <summary>
/// The body of a PDF response, handed to the caller. Disposing it disposes the response and the
/// request, which releases the connection.
/// </summary>
internal sealed class ResponseBodyStream(
  Stream body,
  HttpResponseMessage response,
  HttpRequestMessage request
) : Stream
{
  public override bool CanRead => body.CanRead;

  public override bool CanSeek => false;

  public override bool CanWrite => false;

  public override long Length => throw new NotSupportedException();

  public override long Position
  {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  public override int Read(byte[] buffer, int offset, int count) =>
    body.Read(buffer, offset, count);

  public override int Read(Span<byte> buffer) => body.Read(buffer);

  public override Task<int> ReadAsync(
    byte[] buffer,
    int offset,
    int count,
    CancellationToken cancellationToken
  ) => body.ReadAsync(buffer, offset, count, cancellationToken);

  public override ValueTask<int> ReadAsync(
    Memory<byte> buffer,
    CancellationToken cancellationToken = default
  ) => body.ReadAsync(buffer, cancellationToken);

  public override Task CopyToAsync(
    Stream destination,
    int bufferSize,
    CancellationToken cancellationToken
  ) => body.CopyToAsync(destination, bufferSize, cancellationToken);

  public override void Flush() { }

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  public override void SetLength(long value) => throw new NotSupportedException();

  public override void Write(byte[] buffer, int offset, int count) =>
    throw new NotSupportedException();

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      body.Dispose();
      response.Dispose();
      request.Dispose();
    }

    base.Dispose(disposing);
  }
}
