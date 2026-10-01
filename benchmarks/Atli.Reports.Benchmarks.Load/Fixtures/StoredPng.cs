using System.Buffers.Binary;
using System.Text;

namespace Atli.Reports.Benchmarks.Load.Fixtures;

/// <summary>
/// Encodes RGB images as PNG files whose zlib stream uses only uncompressed ("stored") deflate blocks.
/// </summary>
/// <remarks>
/// Stored blocks keep the output byte-for-byte identical on every platform and runtime (real deflate
/// output depends on the zlib build), and make the file size a direct function of the image size,
/// which is what the asset-heavy fixture needs to control.
/// </remarks>
internal static class StoredPng
{
  private const int MaxStoredBlock = 65_535;
  private static readonly uint[] CrcTable = BuildCrcTable();

  /// <summary>
  /// Encodes a <paramref name="width"/> × <paramref name="height"/> RGB image whose pixels come from
  /// <paramref name="pixel"/>.
  /// </summary>
  public static byte[] Encode(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
  {
    // Raw scanlines: a filter byte (0 = none) followed by RGB triples.
    var stride = (width * 3) + 1;
    var raw = new byte[stride * height];
    for (var y = 0; y < height; y++)
    {
      var row = y * stride;
      for (var x = 0; x < width; x++)
      {
        var (r, g, b) = pixel(x, y);
        var offset = row + 1 + (x * 3);
        raw[offset] = r;
        raw[offset + 1] = g;
        raw[offset + 2] = b;
      }
    }

    using MemoryStream png = new();
    png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

    var header = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(header, width);
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
    header[8] = 8; // bit depth
    header[9] = 2; // color type: truecolor RGB
    WriteChunk(png, "IHDR", header);
    WriteChunk(png, "IDAT", ZlibStored(raw));
    WriteChunk(png, "IEND", []);
    return png.ToArray();
  }

  private static byte[] ZlibStored(byte[] data)
  {
    using MemoryStream zlib = new();
    zlib.WriteByte(0x78); // CMF: deflate, 32K window
    zlib.WriteByte(0x01); // FLG: no dictionary, fastest; (0x7801 % 31 == 0)
    var offset = 0;
    Span<byte> lengths = stackalloc byte[4];
    do
    {
      var length = Math.Min(MaxStoredBlock, data.Length - offset);
      var final = offset + length >= data.Length;
      zlib.WriteByte(final ? (byte)1 : (byte)0); // BFINAL + BTYPE=00 (stored)
      BinaryPrimitives.WriteUInt16LittleEndian(lengths, (ushort)length);
      BinaryPrimitives.WriteUInt16LittleEndian(lengths[2..], (ushort)~length);
      zlib.Write(lengths);
      zlib.Write(data, offset, length);
      offset += length;
    } while (offset < data.Length);

    Span<byte> adler = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(data));
    zlib.Write(adler);
    return zlib.ToArray();
  }

  private static void WriteChunk(Stream png, string type, byte[] data)
  {
    Span<byte> length = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
    png.Write(length);

    var typeBytes = Encoding.ASCII.GetBytes(type);
    png.Write(typeBytes);
    png.Write(data);

    var crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
    Span<byte> crcBytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
    png.Write(crcBytes);
  }

  private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
  {
    foreach (var b in data)
    {
      crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
    }

    return crc;
  }

  private static uint Adler32(ReadOnlySpan<byte> data)
  {
    const uint Modulus = 65_521;
    uint a = 1;
    uint b = 0;
    foreach (var value in data)
    {
      a = (a + value) % Modulus;
      b = (b + a) % Modulus;
    }

    return (b << 16) | a;
  }

  private static uint[] BuildCrcTable()
  {
    var table = new uint[256];
    for (uint n = 0; n < 256; n++)
    {
      var c = n;
      for (var k = 0; k < 8; k++)
      {
        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      }

      table[n] = c;
    }

    return table;
  }
}
