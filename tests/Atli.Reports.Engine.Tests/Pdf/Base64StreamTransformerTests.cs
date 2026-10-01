using System.Security.Cryptography;
using System.Text;
using Atli.Reports.Engine.Pdf;

namespace Atli.Reports.Engine.Tests.Pdf;

public class Base64StreamTransformerTests
{
  [Test]
  [Arguments(1)]
  [Arguments(3)]
  [Arguments(4)]
  [Arguments(7)]
  public async Task Decoding_in_blocks_restores_the_original_bytes(int blockSize)
  {
    var original = Encoding.UTF8.GetBytes(
      "%PDF-1.4 streamed through the transformer, byte by byte."
    );
    var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(original));

    var decoded = Decode(encoded, blockSize, FromBase64TransformMode.DoNotIgnoreWhiteSpaces);

    await Assert.That(decoded).IsEquivalentTo(original);
  }

  [Test]
  public async Task Whitespace_is_skipped_when_ignoring_whitespace()
  {
    var original = Encoding.UTF8.GetBytes("Hello, PDF!");
    var encoded = Encoding.ASCII.GetBytes(
      string.Join("\n ", Convert.ToBase64String(original).Chunk(3).Select(c => new string(c)))
    );

    var decoded = Decode(encoded, 4, FromBase64TransformMode.IgnoreWhiteSpaces);

    await Assert.That(decoded).IsEquivalentTo(original);
  }

  private static byte[] Decode(byte[] encoded, int blockSize, FromBase64TransformMode mode)
  {
    using Base64StreamTransformer transformer = new(mode);
    using MemoryStream output = new();
    var buffer = new byte[encoded.Length];

    for (var index = 0; index < encoded.Length; index += blockSize)
    {
      var count = Math.Min(blockSize, encoded.Length - index);
      var written = transformer.TransformBlock(encoded, index, count, buffer, 0);
      output.Write(buffer, 0, written);
    }

    return output.ToArray();
  }
}
