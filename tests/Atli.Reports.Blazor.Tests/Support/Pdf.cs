using System.Text;

namespace Atli.Reports.Blazor.Tests.Support;

internal static class Pdf
{
  public const string Header = "%PDF-";

  public static string ReadHeader(byte[] document) =>
    Encoding.ASCII.GetString(document, 0, Math.Min(Header.Length, document.Length));
}
