using System.Buffers;

namespace Atli.Reports.Engine.Chromium.Connection;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over arrays rented from <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
internal sealed class PooledBufferWriter(int initialCapacity = 4096)
  : IBufferWriter<byte>,
    IDisposable
{
  private byte[] _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
  private int _written;

  /// <summary>
  /// The bytes written so far.
  /// </summary>
  public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

  public void Advance(int count)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    if (_written + count > _buffer.Length)
    {
      throw new InvalidOperationException("Cannot advance past the end of the buffer.");
    }

    _written += count;
  }

  public Memory<byte> GetMemory(int sizeHint = 0)
  {
    EnsureCapacity(sizeHint);
    return _buffer.AsMemory(_written);
  }

  public Span<byte> GetSpan(int sizeHint = 0)
  {
    EnsureCapacity(sizeHint);
    return _buffer.AsSpan(_written);
  }

  public void Dispose()
  {
    var buffer = _buffer;
    _buffer = [];
    _written = 0;
    if (buffer.Length > 0)
    {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  private void EnsureCapacity(int sizeHint)
  {
    ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
    sizeHint = Math.Max(sizeHint, 1);
    if (_buffer.Length - _written >= sizeHint)
    {
      return;
    }

    var grown = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _written + sizeHint));
    _buffer.AsSpan(0, _written).CopyTo(grown);
    ArrayPool<byte>.Shared.Return(_buffer);
    _buffer = grown;
  }
}
