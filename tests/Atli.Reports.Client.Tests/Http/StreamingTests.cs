using System.Net;
using System.Net.Http.Headers;
using Atli.Reports.Client.Tests.Support;

namespace Atli.Reports.Client.Tests.Http;

/// <summary>
/// The PDF reaches the caller as the server sends it, never buffered whole.
/// </summary>
public class StreamingTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task The_returned_stream_reads_the_pdf_before_the_server_has_finished_sending_it()
  {
    GatedStream body = new("%PDF-1.7 first "u8.ToArray(), "rest %%EOF"u8.ToArray());
    await using var server = StubServer.Start((_, _) => Task.FromResult(PdfResponse(body)));

    var result = await server.Converter.ConvertAsync("<p>x</p>", cancellationToken: TestToken);

    await Assert.That(result.IsT0).IsTrue();
    await using var pdf = result.AsT0;
    await Assert.That(pdf.CanSeek).IsFalse();
    var buffer = new byte[64];
    var first = await pdf.ReadAsync(buffer, TestToken);
    await Assert.That(buffer[..first]).IsEquivalentTo("%PDF-1.7 first "u8.ToArray());

    body.Release();
    using MemoryStream rest = new();
    await pdf.CopyToAsync(rest, TestToken);
    await Assert.That(rest.ToArray()).IsEquivalentTo("rest %%EOF"u8.ToArray());
  }

  [Test]
  public async Task The_destination_receives_the_pdf_before_the_server_has_finished_sending_it()
  {
    GatedStream body = new("%PDF-1.7 first "u8.ToArray(), "rest %%EOF"u8.ToArray());
    await using var server = StubServer.Start((_, _) => Task.FromResult(PdfResponse(body)));
    ReleasingDestination destination = new(body);

    var result = await server.Converter.ConvertAsync("<p>x</p>", destination, null, TestToken);

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(destination.WritesBeforeRelease).IsEqualTo(1);
    await Assert
      .That(destination.ToArray())
      .IsEquivalentTo("%PDF-1.7 first rest %%EOF"u8.ToArray());
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Disposing_the_returned_stream_releases_the_response_and_the_request(
    bool asynchronously
  )
  {
    GatedStream body = new("%PDF-1.7 "u8.ToArray(), "%%EOF"u8.ToArray());
    HttpResponseMessage? response = null;
    await using var server = StubServer.Start(
      (_, _) => Task.FromResult(response = PdfResponse(body))
    );

    var result = await server.Converter.ConvertAsync("<p>x</p>", cancellationToken: TestToken);
    var pdf = result.AsT0;
    if (asynchronously)
    {
      await pdf.DisposeAsync();
    }
    else
    {
      pdf.Dispose();
    }

    // The body goes the way the stream does: disposed asynchronously, it may release its
    // connection without blocking.
    await Assert.That(body.DisposedAsynchronously).IsEqualTo(asynchronously);
    await Assert
      .That(async () => await response!.Content.ReadAsStreamAsync(TestToken))
      .Throws<ObjectDisposedException>();
    await Assert
      .That(async () => await response!.RequestMessage!.Content!.ReadAsStringAsync(TestToken))
      .Throws<ObjectDisposedException>();
    // Disposing it again, either way, does nothing.
    await pdf.DisposeAsync();
    pdf.Dispose();
  }

  private static HttpResponseMessage PdfResponse(Stream body)
  {
    StreamContent content = new(body);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
  }

  /// <summary>
  /// Returns <c>first</c>, then waits for <see cref="Release"/> before returning <c>rest</c>: a
  /// response whose end has not been produced yet.
  /// </summary>
  private sealed class GatedStream(byte[] first, byte[] rest) : Stream
  {
    private readonly TaskCompletionSource _released = new(
      TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _stage;

    public bool IsReleased => _released.Task.IsCompleted;

    /// <summary>
    /// Whether the stream was first disposed asynchronously, once it is disposed.
    /// </summary>
    public bool? DisposedAsynchronously { get; private set; }

    public void Release() => _released.TrySetResult();

    public override async ValueTask<int> ReadAsync(
      Memory<byte> buffer,
      CancellationToken cancellationToken = default
    )
    {
      switch (_stage++)
      {
        case 0:
          first.CopyTo(buffer);
          return first.Length;
        case 1:
          await _released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
          rest.CopyTo(buffer);
          return rest.Length;
        default:
          return 0;
      }
    }

    public override Task<int> ReadAsync(
      byte[] buffer,
      int offset,
      int count,
      CancellationToken cancellationToken
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
      throw new NotSupportedException("Read asynchronously.");

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
      throw new NotSupportedException();

    public override ValueTask DisposeAsync()
    {
      DisposedAsynchronously ??= true;
      return base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
      DisposedAsynchronously ??= false;
      base.Dispose(disposing);
    }
  }

  /// <summary>
  /// Counts the writes that arrive before the source is released, then releases it.
  /// </summary>
  private sealed class ReleasingDestination(GatedStream source) : MemoryStream
  {
    public int WritesBeforeRelease { get; private set; }

    public override async ValueTask WriteAsync(
      ReadOnlyMemory<byte> buffer,
      CancellationToken cancellationToken = default
    )
    {
      if (!source.IsReleased)
      {
        WritesBeforeRelease++;
      }

      await base.WriteAsync(buffer, cancellationToken);
      source.Release();
    }
  }
}
