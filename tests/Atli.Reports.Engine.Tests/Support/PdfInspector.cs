using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// Reads the few facts the tests need from a PDF without a PDF library.
/// </summary>
internal static partial class PdfInspector
{
  public static bool HasPdfHeader(ReadOnlySpan<byte> pdf) => pdf.StartsWith("%PDF-"u8);

  public static bool HasEofMarker(ReadOnlySpan<byte> pdf) =>
    pdf.TrimEnd("\r\n "u8).EndsWith("%%EOF"u8);

  /// <summary>
  /// Counts the page objects (<c>/Type /Page</c>, not <c>/Type /Pages</c>).
  /// </summary>
  public static int CountPages(byte[] pdf) => PageObject().Count(Encoding.Latin1.GetString(pdf));

  /// <summary>
  /// Reads the document title Chromium writes into the PDF information dictionary, either as a
  /// literal string or as UTF-16BE hex.
  /// </summary>
  public static string? ReadTitle(byte[] pdf)
  {
    var text = Encoding.Latin1.GetString(pdf);
    var match = TitleEntry().Match(text);
    if (!match.Success)
    {
      return null;
    }

    if (match.Groups["literal"].Success)
    {
      return match.Groups["literal"].Value;
    }

    var hex = match.Groups["hex"].Value;
    var bytes = Convert.FromHexString(hex);
    return bytes is [0xFE, 0xFF, ..]
      ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
      : Encoding.Latin1.GetString(bytes);
  }

  public static string Describe(byte[] pdf) =>
    string.Create(CultureInfo.InvariantCulture, $"{pdf.Length} bytes, title '{ReadTitle(pdf)}'");

  [GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])")]
  private static partial Regex PageObject();

  [GeneratedRegex(@"/Title\s*(?:\((?<literal>[^)]*)\)|<(?<hex>[0-9A-Fa-f]*)>)")]
  private static partial Regex TitleEntry();
}
