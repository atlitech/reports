using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Atli.Reports.Client.Tests.Support;

/// <summary>
/// Reads the few facts the tests need from a PDF without a PDF library.
/// </summary>
internal static partial class Pdf
{
  public static bool IsComplete(ReadOnlySpan<byte> pdf) =>
    pdf.StartsWith("%PDF-"u8) && pdf.TrimEnd("\r\n "u8).EndsWith("%%EOF"u8);

  /// <summary>
  /// Counts the page objects (<c>/Type /Page</c>, not <c>/Type /Pages</c>).
  /// </summary>
  public static int CountPages(byte[] pdf) => PageObject().Count(Encoding.Latin1.GetString(pdf));

  /// <summary>
  /// The page sizes, in points, from the pages' <c>/MediaBox</c> entries.
  /// </summary>
  public static IReadOnlyList<(double Width, double Height)> PageSizes(byte[] pdf) =>
    [
      .. MediaBox()
        .Matches(Encoding.Latin1.GetString(pdf))
        .Select(match =>
          (
            Width: Math.Round(
              double.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture)
            ),
            Height: Math.Round(
              double.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture)
            )
          )
        ),
    ];

  [GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])")]
  private static partial Regex PageObject();

  [GeneratedRegex(@"/MediaBox\s*\[\s*0\s+0\s+(?<width>[0-9.]+)\s+(?<height>[0-9.]+)\s*\]")]
  private static partial Regex MediaBox();
}
