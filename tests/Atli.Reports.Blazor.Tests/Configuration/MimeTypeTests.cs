using Atli.Reports.Blazor.Helpers;

namespace Atli.Reports.Blazor.Tests.Configuration;

/// <summary>
/// The MIME types of the <c>data:</c> URIs that assets become.
/// </summary>
public class MimeTypeTests
{
  [Test]
  [Arguments("logo.svg", "image/svg+xml")]
  [Arguments("logo.png", "image/png")]
  [Arguments("photo.JPG", "image/jpeg")]
  [Arguments("photo.webp", "image/webp")]
  [Arguments("font.woff2", "font/woff2")]
  [Arguments("notes.txt", "text/plain")]
  public async Task Known_extensions_get_their_mime_type(string fileName, string expected)
  {
    await Assert.That(MimeTypes.GetMimeType(fileName)).IsEqualTo(expected);
  }

  [Test]
  [Arguments("archive.unknownextension")]
  [Arguments("LICENSE")]
  public async Task Other_files_are_octet_streams(string fileName)
  {
    await Assert.That(MimeTypes.GetMimeType(fileName)).IsEqualTo("application/octet-stream");
  }
}
