using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Atli.Reports.Engine.Chromium.Connection;

/// <summary>
/// The <c>result</c> object of a successful DevTools command, held in a pooled buffer.
/// </summary>
/// <remarks>
/// Dispose the reply as soon as the result has been read, so the buffer returns to the pool.
/// </remarks>
internal sealed class DevToolsReply : IDisposable
{
  private static readonly byte[] EmptyResult = "{}"u8.ToArray();

  private byte[]? _buffer;
  private readonly int _length;
  private readonly bool _pooled;

  private DevToolsReply(byte[] buffer, int length, bool pooled)
  {
    _buffer = buffer;
    _length = length;
    _pooled = pooled;
  }

  /// <summary>
  /// The UTF-8 JSON of the <c>result</c> object.
  /// </summary>
  public ReadOnlySpan<byte> Result
  {
    get
    {
      ObjectDisposedException.ThrowIf(_buffer is null, this);
      return _buffer.AsSpan(0, _length);
    }
  }

  /// <summary>
  /// Copies <paramref name="result"/> into a pooled buffer.
  /// </summary>
  public static DevToolsReply Create(ReadOnlySpan<byte> result)
  {
    if (result.IsEmpty)
    {
      return new DevToolsReply(EmptyResult, EmptyResult.Length, pooled: false);
    }

    var buffer = ArrayPool<byte>.Shared.Rent(result.Length);
    result.CopyTo(buffer);
    return new DevToolsReply(buffer, result.Length, pooled: true);
  }

  /// <summary>
  /// Deserializes the result with source-generated metadata.
  /// </summary>
  public T Deserialize<T>(JsonTypeInfo<T> typeInfo) =>
    JsonSerializer.Deserialize(Result, typeInfo)
    ?? throw new JsonException($"The DevTools result could not be read as {typeof(T).Name}.");

  /// <summary>
  /// Disposes the reply of a command nobody waits for any more as soon as it arrives, so its buffer
  /// returns to the pool. A failed command's exception is observed and dropped.
  /// </summary>
  public static void DisposeWhenReady(Task<DevToolsReply> reply) =>
    _ = reply.ContinueWith(
      static task =>
      {
        if (task.IsCompletedSuccessfully)
        {
          task.Result.Dispose();
        }
        else
        {
          _ = task.Exception;
        }
      },
      CancellationToken.None,
      TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default
    );

  public void Dispose()
  {
    var buffer = Interlocked.Exchange(ref _buffer, null);
    if (buffer is not null && _pooled)
    {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }
}
