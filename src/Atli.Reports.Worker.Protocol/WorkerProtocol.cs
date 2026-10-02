using System.Buffers.Binary;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Atli.Reports.Worker.Protocol;

/// <summary>
/// Bounded little-endian length-prefixed frames. PDF completion is an explicit zero frame, independent
/// of process exit so a trusted benchmark may reuse a worker sequentially. Production gateways must
/// additionally validate job identity, their output budget, and the configured worker lifecycle.
/// </summary>
public static class WorkerProtocol
{
  public const int Version = 1;
  public const int MaxRequestBytes = 16 * 1024 * 1024;
  public const int MaxHeaderBytes = 16 * 1024;
  public const int MaxChunkBytes = 64 * 1024;
  public const string PdfStatus = "pdf";
  public const string ErrorStatus = "error";

  private static readonly WorkerJsonContext JsonContext = new(
    new JsonSerializerOptions(WorkerJsonContext.Default.Options)
    {
      // This JSON is a framed machine protocol, never embedded in HTML or script.
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      AllowDuplicateProperties = false,
      RespectRequiredConstructorParameters = true,
    }
  );

  public static async Task WriteRequestAsync(
    Stream output,
    WorkerRequest request,
    CancellationToken cancellationToken
  )
  {
    Validate(request);
    await WriteJsonAsync(
      output,
      request,
      JsonContext.WorkerRequest,
      MaxRequestBytes,
      cancellationToken
    );
  }

  public static async Task<WorkerRequest> ReadRequestAsync(
    Stream input,
    CancellationToken cancellationToken
  ) =>
    await TryReadRequestAsync(input, cancellationToken)
    ?? throw new InvalidDataException("The worker request is missing.");

  /// <summary>Returns null only for clean EOF before a new request frame; partial frames always fail.</summary>
  public static async Task<WorkerRequest?> TryReadRequestAsync(
    Stream input,
    CancellationToken cancellationToken
  )
  {
    var length = await ReadLengthAsync(
      input,
      MaxRequestBytes,
      allowZero: false,
      allowEof: true,
      cancellationToken
    );
    if (length is null)
    {
      return null;
    }

    var request = await ReadJsonAsync(
      input,
      length.Value,
      JsonContext.WorkerRequest,
      cancellationToken
    );
    Validate(request);
    return request;
  }

  public static async Task WriteResponseHeaderAsync(
    Stream output,
    WorkerResponseHeader header,
    CancellationToken cancellationToken
  )
  {
    Validate(header);
    await WriteJsonAsync(
      output,
      header,
      JsonContext.WorkerResponseHeader,
      MaxHeaderBytes,
      cancellationToken
    );
  }

  public static async Task<WorkerResponseHeader> ReadResponseHeaderAsync(
    Stream input,
    CancellationToken cancellationToken
  )
  {
    var length = await ReadLengthAsync(
      input,
      MaxHeaderBytes,
      allowZero: false,
      allowEof: false,
      cancellationToken
    );
    var header = await ReadJsonAsync(
      input,
      length!.Value,
      JsonContext.WorkerResponseHeader,
      cancellationToken
    );
    Validate(header);
    return header;
  }

  /// <summary>Writes one nonempty bounded PDF chunk. Empty frames are reserved for successful completion.</summary>
  public static Task WritePdfChunkAsync(
    Stream output,
    ReadOnlyMemory<byte> chunk,
    CancellationToken cancellationToken
  )
  {
    if (chunk.Length is < 1 or > MaxChunkBytes)
    {
      throw new InvalidDataException("The worker PDF chunk length is invalid.");
    }
    return WriteFrameAsync(output, chunk, cancellationToken);
  }

  public static Task CompletePdfAsync(Stream output, CancellationToken cancellationToken) =>
    WriteFrameAsync(output, ReadOnlyMemory<byte>.Empty, cancellationToken);

  /// <summary>
  /// Reads into a reusable buffer of at least 64 KiB. Zero denotes successful completion. Callers
  /// returning pooled buffers must clear them because their contents may include private PDF data.
  /// </summary>
  public static async Task<int> ReadPdfChunkAsync(
    Stream input,
    Memory<byte> destination,
    CancellationToken cancellationToken
  )
  {
    if (destination.Length < MaxChunkBytes)
    {
      throw new ArgumentException(
        "The PDF buffer must hold a complete protocol chunk.",
        nameof(destination)
      );
    }
    var length = await ReadLengthAsync(
      input,
      MaxChunkBytes,
      allowZero: true,
      allowEof: false,
      cancellationToken
    );
    await ReadExactlyAsync(input, destination[..length!.Value], cancellationToken);
    return length.Value;
  }

  /// <summary>Convenience allocating reader for tests and small harnesses; gateways should reuse a buffer.</summary>
  public static async Task<byte[]> ReadPdfChunkAsync(
    Stream input,
    CancellationToken cancellationToken
  )
  {
    var length = await ReadLengthAsync(
      input,
      MaxChunkBytes,
      allowZero: true,
      allowEof: false,
      cancellationToken
    );
    var bytes = new byte[length!.Value];
    await ReadExactlyAsync(input, bytes, cancellationToken);
    return bytes;
  }

  private static void Validate(WorkerRequest request)
  {
    if (request.Version != Version || request.JobId == Guid.Empty || request.Html is null)
    {
      throw new InvalidDataException("The worker request contract is invalid.");
    }
  }

  private static void Validate(WorkerResponseHeader header)
  {
    if (
      header.Version != Version
      || header.JobId == Guid.Empty
      || (
        header.Status == PdfStatus
          ? header.Kind is not null
          : header.Status != ErrorStatus || header.Kind is null || !IsWorkerError(header.Kind.Value)
      )
    )
    {
      throw new InvalidDataException("The worker response contract is invalid.");
    }
  }

  private static bool IsWorkerError(Atli.Reports.Engine.ConversionErrorKind kind) =>
    kind
      is Atli.Reports.Engine.ConversionErrorKind.InvalidRequest
        or Atli.Reports.Engine.ConversionErrorKind.SignalTimeout
        or Atli.Reports.Engine.ConversionErrorKind.PolicyDenied
        or Atli.Reports.Engine.ConversionErrorKind.Busy
        or Atli.Reports.Engine.ConversionErrorKind.Timeout
        or Atli.Reports.Engine.ConversionErrorKind.Canceled
        or Atli.Reports.Engine.ConversionErrorKind.BrowserUnavailable
        or Atli.Reports.Engine.ConversionErrorKind.RenderFailed;

  private static async Task WriteJsonAsync<T>(
    Stream output,
    T value,
    JsonTypeInfo<T> metadata,
    int limit,
    CancellationToken cancellationToken
  )
  {
    using BoundedBuffer buffer = new(limit);
    try
    {
      await JsonSerializer.SerializeAsync(buffer, value, metadata, cancellationToken);
    }
    catch (Exception exception) when (exception is JsonException or ArgumentException)
    {
      throw new InvalidDataException("The worker JSON value is invalid.");
    }
    await WriteFrameAsync(
      output,
      buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)),
      cancellationToken
    );
  }

  private static async Task<T> ReadJsonAsync<T>(
    Stream input,
    int length,
    JsonTypeInfo<T> metadata,
    CancellationToken cancellationToken
  )
  {
    var bytes = new byte[length];
    try
    {
      await ReadExactlyAsync(input, bytes, cancellationToken);
      return JsonSerializer.Deserialize(bytes, metadata)
        ?? throw new InvalidDataException("The worker JSON frame is empty.");
    }
    catch (JsonException)
    {
      // Do not propagate JSON paths, source snippets, or document-controlled text across boundaries.
      throw new InvalidDataException("The worker JSON frame is invalid.");
    }
    finally
    {
      Array.Clear(bytes);
    }
  }

  private static async Task<int?> ReadLengthAsync(
    Stream input,
    int limit,
    bool allowZero,
    bool allowEof,
    CancellationToken cancellationToken
  )
  {
    var prefix = new byte[4];
    var first = await input.ReadAsync(prefix.AsMemory(0, 1), cancellationToken);
    if (first == 0 && allowEof)
    {
      return null;
    }
    if (first == 0)
    {
      throw new InvalidDataException("The worker frame is truncated.");
    }
    await ReadExactlyAsync(input, prefix.AsMemory(1), cancellationToken);
    var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
    if (length < (allowZero ? 0 : 1) || length > limit)
    {
      throw new InvalidDataException("The worker frame length is invalid.");
    }
    return length;
  }

  private static async Task ReadExactlyAsync(
    Stream input,
    Memory<byte> bytes,
    CancellationToken cancellationToken
  )
  {
    try
    {
      await input.ReadExactlyAsync(bytes, cancellationToken);
    }
    catch (EndOfStreamException)
    {
      throw new InvalidDataException("The worker frame is truncated.");
    }
  }

  private static async Task WriteFrameAsync(
    Stream output,
    ReadOnlyMemory<byte> bytes,
    CancellationToken cancellationToken
  )
  {
    var prefix = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
    await output.WriteAsync(prefix, cancellationToken);
    await output.WriteAsync(bytes, cancellationToken);
    await output.FlushAsync(cancellationToken);
  }

  private sealed class BoundedBuffer(int limit) : MemoryStream
  {
    public override void Write(byte[] buffer, int offset, int count)
    {
      EnsureCapacityForWrite(count);
      base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
      EnsureCapacityForWrite(buffer.Length);
      var start = checked((int)Position);
      var end = start + buffer.Length;
      if (end > Length)
      {
        base.SetLength(end);
      }
      buffer.CopyTo(GetBuffer().AsSpan(start));
      Position = end;
    }

    private void EnsureCapacityForWrite(int count)
    {
      if (Position + count > limit)
      {
        throw new InvalidDataException("The worker JSON frame exceeds its limit.");
      }
    }

    public override ValueTask WriteAsync(
      ReadOnlyMemory<byte> buffer,
      CancellationToken cancellationToken = default
    )
    {
      cancellationToken.ThrowIfCancellationRequested();
      Write(buffer.Span);
      return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(
      byte[] buffer,
      int offset,
      int count,
      CancellationToken cancellationToken
    ) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        Array.Clear(GetBuffer());
      }
      base.Dispose(disposing);
    }
  }
}
