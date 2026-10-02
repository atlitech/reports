using Atli.Reports.Worker.Protocol;

namespace Atli.Reports.Worker;

/// <summary>Frames the PDF without buffering it; the caller controls the final success terminator.</summary>
internal sealed class WorkerPdfStream(Stream output, Guid jobId) : Stream
{
  public bool HasStarted { get; private set; }
  private bool _completed;

  public override bool CanRead => false;
  public override bool CanSeek => false;
  public override bool CanWrite => !_completed;
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
    ObjectDisposedException.ThrowIf(_completed, this);
    if (buffer.IsEmpty)
    {
      return;
    }
    await StartAsync(cancellationToken);
    while (!buffer.IsEmpty)
    {
      var count = Math.Min(buffer.Length, WorkerProtocol.MaxChunkBytes);
      await WorkerProtocol.WritePdfChunkAsync(output, buffer[..count], cancellationToken);
      buffer = buffer[count..];
    }
  }

  private async Task StartAsync(CancellationToken cancellationToken)
  {
    if (!HasStarted)
    {
      HasStarted = true;
      await WorkerProtocol.WriteResponseHeaderAsync(
        output,
        new WorkerResponseHeader(WorkerProtocol.Version, jobId, WorkerProtocol.PdfStatus),
        cancellationToken
      );
    }
  }

  public async Task CompleteAsync(CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(_completed, this);
    await StartAsync(cancellationToken);
    await WorkerProtocol.CompletePdfAsync(output, cancellationToken);
    _completed = true;
  }

  public override Task WriteAsync(
    byte[] buffer,
    int offset,
    int count,
    CancellationToken cancellationToken
  ) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

  public override Task FlushAsync(CancellationToken cancellationToken) =>
    HasStarted ? output.FlushAsync(cancellationToken) : Task.CompletedTask;

  public override void Flush() => throw new NotSupportedException("Use asynchronous worker I/O.");

  public override void Write(byte[] buffer, int offset, int count) =>
    throw new NotSupportedException("Use asynchronous worker I/O.");

  public override int Read(byte[] buffer, int offset, int count) =>
    throw new NotSupportedException();

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  public override void SetLength(long value) => throw new NotSupportedException();
}
