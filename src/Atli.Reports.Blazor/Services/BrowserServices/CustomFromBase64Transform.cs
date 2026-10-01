using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Atli.Reports.Blazor.Services.BrowserServices;

internal sealed class CustomFromBase64Transform(FromBase64TransformMode whitespaces) : IDisposable
{
  private byte[] _inputBuffer = new byte[4];
  private int _inputIndex;

  // A buffer with size 32 is stack allocated, to cover common cases and benefit from JIT's optimizations.
  private const int StackAllocSize = 32;

  // Converting from Base64 generates 3 bytes output from each 4 bytes input block
  public static int InputBlockSize => 4;
  public static int OutputBlockSize => 3;

  public int TransformBlock(
    ReadOnlySpan<byte> inputBuffer,
    int inputOffset,
    int inputCount,
    Span<byte> outputBuffer,
    int outputOffset
  )
  {
    // inputCount != InputBlockSize is allowed
    ObjectDisposedException.ThrowIf(_inputBuffer == null, typeof(FromBase64Transform));

    var inputBufferSpan = inputBuffer.Slice(inputOffset, inputCount);
    var bytesToTransform = _inputIndex + inputBufferSpan.Length;

    byte[]? transformBufferArray = null;
    Span<byte> transformBuffer = stackalloc byte[StackAllocSize];
    if (bytesToTransform > StackAllocSize)
    {
      transformBuffer = transformBufferArray = CryptoPool.Rent(inputCount);
    }

    transformBuffer = AppendInputBuffers(inputBufferSpan, transformBuffer);
    // update bytesToTransform since it can be less if some whitespace was discarded.
    bytesToTransform = transformBuffer.Length;

    // Too little data to decode: save data to _inputBuffer, so it can be transformed later
    if (bytesToTransform < InputBlockSize)
    {
      transformBuffer.CopyTo(_inputBuffer);

      _inputIndex = bytesToTransform;

      ReturnToCryptoPool(transformBufferArray, transformBuffer.Length);

      return 0;
    }

    ConvertFromBase64(transformBuffer, outputBuffer[outputOffset..], out var written);

    ReturnToCryptoPool(transformBufferArray, transformBuffer.Length);

    return written;
  }

  private Span<byte> AppendInputBuffers(ReadOnlySpan<byte> inputBuffer, Span<byte> transformBuffer)
  {
    _inputBuffer.AsSpan(0, _inputIndex).CopyTo(transformBuffer);

    if (whitespaces == FromBase64TransformMode.DoNotIgnoreWhiteSpaces)
    {
      inputBuffer.CopyTo(transformBuffer[_inputIndex..]);
      return transformBuffer[..(_inputIndex + inputBuffer.Length)];
    }

    var count = _inputIndex;
    for (var i = 0; i < inputBuffer.Length; i++)
    {
      if (!IsWhitespace(inputBuffer[i]))
      {
        transformBuffer[count++] = inputBuffer[i];
      }
    }

    return transformBuffer[..count];
  }

  private static bool IsWhitespace(byte value)
  {
    // We assume ASCII encoded data. If there is any non-ASCII char, it is invalid
    // Base64 and will be caught during decoding.

    // SPACE        32
    // TAB           9
    // LF           10
    // VTAB         11
    // FORM FEED    12
    // CR           13

    return value == 32 || ((uint)value - 9 <= (13 - 9));
  }

  private void ConvertFromBase64(
    Span<byte> transformBuffer,
    Span<byte> outputBuffer,
    out int written
  )
  {
    var bytesToTransform = transformBuffer.Length;
    Debug.Assert(bytesToTransform >= 4);

    // Save data that won't be transformed to _inputBuffer, so it can be transformed later
    _inputIndex = bytesToTransform & 3; // bit hack for % 4
    bytesToTransform -= _inputIndex; // only transform up to the next multiple of 4
    Debug.Assert(_inputIndex < _inputBuffer.Length);
    transformBuffer[^_inputIndex..].CopyTo(_inputBuffer);

    transformBuffer = transformBuffer[..bytesToTransform];
    var status = Base64.DecodeFromUtf8(
      transformBuffer,
      outputBuffer,
      out var consumed,
      out written
    );

    if (status == OperationStatus.Done)
    {
      Debug.Assert(consumed == bytesToTransform);
    }
    else
    {
      Debug.Assert(status == OperationStatus.InvalidData);
    }
  }

  private static void ReturnToCryptoPool(byte[]? array, int clearSize)
  {
    if (array != null)
    {
      CryptoPool.Return(array, clearSize);
    }
  }

  // Reset the state of the transform so it can be used again
  public void Reset()
  {
    _inputIndex = 0;
  }

  // must implement IDisposable, which in this case means clearing the input buffer

  public void Dispose()
  {
    Dispose(true);
    GC.SuppressFinalize(this);
  }

  private void Dispose(bool disposing)
  {
    // we always want to clear the input buffer
    if (disposing)
    {
      CryptographicOperations.ZeroMemory(_inputBuffer);
      _inputBuffer = null!;

      Reset();
    }
  }

  ~CustomFromBase64Transform()
  {
    Dispose(false);
  }
}

internal static class CryptoPool
{
  private const int ClearAll = -1;

  internal static byte[] Rent(int minimumLength)
  {
    return ArrayPool<byte>.Shared.Rent(minimumLength);
  }

  internal static void Return(byte[] array, int clearSize = ClearAll)
  {
    Debug.Assert(clearSize <= array.Length);
    var clearWholeArray = clearSize < 0;

    if (!clearWholeArray && clearSize != 0)
    {
#if (NETCOREAPP || NETSTANDARD2_1) && !CP_NO_ZEROMEMORY
      CryptographicOperations.ZeroMemory(array.AsSpan(0, clearSize));
#else
      Array.Clear(array, 0, clearSize);
#endif
    }

    ArrayPool<byte>.Shared.Return(array, clearWholeArray);
  }
}
