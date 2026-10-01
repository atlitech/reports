using System.Text;
using Atli.Reports.Engine.Pdf;

namespace Atli.Reports.Engine.Tests.Pdf;

public class PdfReadResultTests
{
  [Test]
  public async Task Base64_data_is_decoded()
  {
    var chunk = Decode("""{"base64Encoded":true,"data":"JVBERi0xLjQ=","eof":false}""");

    await Assert.That(Text(chunk)).IsEqualTo("%PDF-1.4");
    await Assert.That(chunk.Eof).IsFalse();
    chunk.Return();
  }

  [Test]
  public async Task The_last_chunk_keeps_its_data()
  {
    // The final read may carry bytes and the end-of-file flag together; dropping them truncates the PDF.
    var chunk = Decode("""{"eof":true,"data":"JSVFT0Y=","base64Encoded":true}""");

    await Assert.That(Text(chunk)).IsEqualTo("%%EOF");
    await Assert.That(chunk.Eof).IsTrue();
    chunk.Return();
  }

  [Test]
  public async Task Escaped_base64_is_unescaped_before_decoding()
  {
    var chunk = Decode("""{"base64Encoded":true,"data":"JVBERi0xLjQ\/","eof":false}""");

    await Assert.That(chunk.Length).IsEqualTo(9);
    chunk.Return();
  }

  [Test]
  public async Task Text_data_is_copied_as_is()
  {
    var chunk = Decode("""{"base64Encoded":false,"data":"plain","eof":true}""");

    await Assert.That(Text(chunk)).IsEqualTo("plain");
    chunk.Return();
  }

  [Test]
  public async Task An_empty_end_of_file_read_has_no_data()
  {
    var chunk = Decode("""{"base64Encoded":true,"data":"","eof":true}""");

    await Assert.That(chunk.Length).IsEqualTo(0);
    await Assert.That(chunk.Eof).IsTrue();
  }

  [Test]
  public async Task Malformed_base64_is_rejected()
  {
    await Assert
      .That(() => Decode("""{"base64Encoded":true,"data":"not base64!","eof":true}"""))
      .Throws<InvalidDataException>();
  }

  private static ChromiumPdfGenerator.PdfChunk Decode(string json) =>
    ChromiumPdfGenerator.DecodeReadResult(Encoding.UTF8.GetBytes(json));

  private static string Text(ChromiumPdfGenerator.PdfChunk chunk) =>
    Encoding.UTF8.GetString(chunk.Buffer!, 0, chunk.Length);
}
