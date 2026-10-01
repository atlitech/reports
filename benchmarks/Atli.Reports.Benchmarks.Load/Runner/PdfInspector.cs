using System.Globalization;
using System.Text;

namespace Atli.Reports.Benchmarks.Load.Runner;

/// <summary>
/// Checks that a response body is a complete PDF and counts its pages.
/// </summary>
internal static class PdfInspector
{
  private static readonly byte[] Header = "%PDF-"u8.ToArray();
  private static readonly byte[] EndOfFile = "%%EOF"u8.ToArray();
  private static readonly byte[] CountKey = "/Count"u8.ToArray();

  /// <summary>
  /// Returns the page count of <paramref name="pdf"/>, or <see langword="null"/> when it is not a
  /// complete PDF (missing <c>%PDF-</c> header or <c>%%EOF</c> trailer).
  /// </summary>
  /// <remarks>
  /// The page count is the largest <c>/Count</c> in the file, which is the root of the page tree.
  /// Chromium writes page-tree dictionaries uncompressed and adds no outline unless asked, so the
  /// largest <c>/Count</c> is the number of pages. Returns 0 for a valid PDF without a page tree.
  /// </remarks>
  public static int? CountPages(ReadOnlySpan<byte> pdf)
  {
    if (!pdf.StartsWith(Header))
    {
      return null;
    }

    var tail = pdf[Math.Max(0, pdf.Length - 1024)..];
    if (tail.IndexOf(EndOfFile) < 0)
    {
      return null;
    }

    var pages = 0;
    var remaining = pdf;
    int index;
    while ((index = remaining.IndexOf(CountKey)) >= 0)
    {
      remaining = remaining[(index + CountKey.Length)..];
      var digits = remaining.TrimStart(" \r\n\t"u8);
      var length = 0;
      while (length < digits.Length && length < 9 && char.IsAsciiDigit((char)digits[length]))
      {
        length++;
      }

      if (
        length > 0
        && int.TryParse(
          Encoding.ASCII.GetString(digits[..length]),
          NumberStyles.None,
          CultureInfo.InvariantCulture,
          out var count
        )
      )
      {
        pages = Math.Max(pages, count);
      }
    }

    return pages;
  }
}
