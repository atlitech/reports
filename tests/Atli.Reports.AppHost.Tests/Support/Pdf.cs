using System.Text;
using System.Text.RegularExpressions;

namespace Atli.Reports.AppHost.Tests.Support;

/// <summary>
/// Reads the few facts the tests need from a PDF without a PDF library.
/// </summary>
internal static partial class Pdf
{
  /// <summary>
  /// Whether the bytes are a whole PDF: the <c>%PDF-</c> header and the <c>%%EOF</c> trailer.
  /// </summary>
  public static bool IsComplete(ReadOnlySpan<byte> pdf) =>
    pdf.StartsWith("%PDF-"u8) && pdf.TrimEnd("\r\n "u8).EndsWith("%%EOF"u8);

  /// <summary>
  /// Counts the page objects (<c>/Type /Page</c>, not <c>/Type /Pages</c>).
  /// </summary>
  public static int CountPages(byte[] pdf) => PageObject().Count(Encoding.Latin1.GetString(pdf));

  [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])")]
  private static partial Regex PageObject();
}
