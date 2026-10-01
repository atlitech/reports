namespace Atli.Reports.Blazor.Helpers;

/// <summary>
/// Provides a set of methods for working with MIME types.
/// </summary>
internal static class MimeTypes
{
  private static readonly Dictionary<string, string> MimeTypesDictionary = new()
  {
    { ".txt", "text/plain" },
    { ".pdf", "application/pdf" },
    { ".csv", "text/csv" },
    { ".png", "image/png" },
    { ".jpg", "image/jpeg" },
    { ".jpeg", "image/jpeg" },
    { ".gif", "image/gif" },
    { ".webp", "image/webp" },
  };

  private const string UnknownMimeType = "application/octet-stream";

  /// <summary>
  /// Gets the MIME type for the specified file name.
  /// </summary>
  /// <param name="fileName">The file name.</param>
  /// <returns>The MIME type.</returns>
  /// <exception cref="ArgumentException">Thrown when <paramref name="fileName"/> is null or empty, or when the MIME type is not registered for the specified file extension.</exception>
  public static string GetMimeType(string fileName)
  {
    var extension = Path.GetExtension(fileName).ToLowerInvariant();

    return string.IsNullOrEmpty(extension)
      ? UnknownMimeType
      : MimeTypesDictionary.GetValueOrDefault(extension, UnknownMimeType);
  }
}
