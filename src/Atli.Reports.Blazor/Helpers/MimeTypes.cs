using Microsoft.AspNetCore.StaticFiles;

namespace Atli.Reports.Blazor.Helpers;

/// <summary>
/// Provides a set of methods for working with MIME types.
/// </summary>
internal static class MimeTypes
{
  /// <summary>
  /// ASP.NET Core's table of extensions to MIME types, the one the static files middleware serves with.
  /// </summary>
  private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

  private const string UnknownMimeType = "application/octet-stream";

  /// <summary>
  /// Gets the MIME type for the specified file name.
  /// </summary>
  /// <param name="fileName">The file name.</param>
  /// <returns>
  /// The MIME type, or <c>application/octet-stream</c> when the file name has no extension or an
  /// unknown one.
  /// </returns>
  public static string GetMimeType(string fileName)
  {
    return ContentTypeProvider.TryGetContentType(fileName, out var contentType)
      ? contentType
      : UnknownMimeType;
  }
}
