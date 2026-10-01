using System.Buffers.Binary;
using System.Text;

namespace Atli.Reports.Engine.Tests.Support;

internal static class TestDocuments
{
  /// <summary>
  /// An HTML document of more than 1 MB: a page with an inline base64 bitmap of noise, which
  /// compresses badly, so the PDF is large too.
  /// </summary>
  public static string LargeImageDocument(int size = 600)
  {
    var bitmap = NoiseBitmap(size, size);
    return "<!DOCTYPE html><html><body><h1>Large</h1><img style=\"width:6in\" src=\"data:image/bmp;base64,"
      + Convert.ToBase64String(bitmap)
      + "\"></body></html>";
  }

  /// <summary>
  /// A document of <paramref name="pages"/> pages, one paragraph each.
  /// </summary>
  public static string ManyPagesDocument(int pages)
  {
    StringBuilder html = new(
      "<!DOCTYPE html><html><head><style>section{page-break-after:always}</style></head><body>"
    );
    for (var page = 1; page <= pages; page++)
    {
      html.Append("<section><h2>Page ").Append(page).Append("</h2><p>");
      html.Append(
        string.Concat(
          Enumerable.Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit. ", 40)
        )
      );
      html.Append("</p></section>");
    }

    return html.Append("</body></html>").ToString();
  }

  private static byte[] NoiseBitmap(int width, int height)
  {
    var rowSize = (width * 3 + 3) & ~3;
    var pixelBytes = rowSize * height;
    var bitmap = new byte[54 + pixelBytes];
    bitmap[0] = (byte)'B';
    bitmap[1] = (byte)'M';
    BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(2), bitmap.Length);
    BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(10), 54);
    BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(14), 40);
    BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(18), width);
    BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(22), height);
    BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(26), 1);
    BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(28), 24);
    BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(34), pixelBytes);
    new Random(42).NextBytes(bitmap.AsSpan(54));
    return bitmap;
  }
}
