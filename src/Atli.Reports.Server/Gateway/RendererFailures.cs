using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Reads a renderer's error response, which may come from a compromised renderer: at most
/// <see cref="MaxBodyBytes"/> of it, and only the members the gateway needs.
/// </summary>
internal static class RendererFailures
{
  /// <summary>The most of an error body the gateway reads.</summary>
  public const int MaxBodyBytes = 16 * 1024;

  /// <summary>The longest renderer detail passed on to the caller.</summary>
  public const int MaxDetailLength = 512;

  /// <summary>
  /// What the Azure Container Apps Sandboxes proxy answers, with <c>403</c>, for a suspended
  /// sandbox. The renderer's own <c>403</c> is problem details, so the two cannot be confused.
  /// </summary>
  private const string SandboxNotRunning = "Sandbox is not running";

  public static async Task<RendererFailure> ReadAsync(
    HttpResponseMessage response,
    CancellationToken cancellationToken
  )
  {
    var status = (int)response.StatusCode;
    var isProblem = string.Equals(
      response.Content.Headers.ContentType?.MediaType,
      "application/problem+json",
      StringComparison.OrdinalIgnoreCase
    );
    var body = await ReadBodyAsync(response, cancellationToken);
    if (body is null)
    {
      return new RendererFailure(status, isProblem, null, null, false);
    }

    try
    {
      using var document = JsonDocument.Parse(body.Value);
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return new RendererFailure(status, isProblem, null, null, false);
      }

      if (isProblem)
      {
        return new RendererFailure(
          status,
          true,
          ReadString(root, "kind"),
          ReadString(root, "detail"),
          false
        );
      }

      var notRunning =
        response.StatusCode == HttpStatusCode.Forbidden
        && ReadString(root, "error") == SandboxNotRunning;
      return new RendererFailure(status, false, null, null, notRunning);
    }
    catch (JsonException)
    {
      return new RendererFailure(status, isProblem, null, null, false);
    }
  }

  /// <summary>
  /// The <see cref="ConversionErrorKind"/> a problem's <c>kind</c> names, if it names one other than
  /// <see cref="ConversionErrorKind.Canceled"/>, which only the caller's own cancellation produces.
  /// </summary>
  public static ConversionErrorKind? ParseKind(string? kind) =>
    kind is { Length: > 0 }
    && char.IsAsciiLetter(kind[0])
    && Enum.TryParse<ConversionErrorKind>(kind, out var parsed)
    && Enum.IsDefined(parsed)
    && parsed != ConversionErrorKind.Canceled
      ? parsed
      : null;

  /// <summary>
  /// <paramref name="detail"/> without control or formatting characters (which could rewrite a
  /// terminal or reorder text in a log viewer), trimmed and cut to <see cref="MaxDetailLength"/>,
  /// or <see langword="null"/> when nothing is left.
  /// </summary>
  public static string? Sanitize(string? detail)
  {
    if (detail is null)
    {
      return null;
    }

    StringBuilder builder = new(Math.Min(detail.Length, MaxDetailLength));
    foreach (var c in detail)
    {
      if (
        char.IsControl(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format
        || (char.IsWhiteSpace(c) && builder.Length == 0)
      )
      {
        continue;
      }

      builder.Append(c);
      if (builder.Length == MaxDetailLength)
      {
        break;
      }
    }

    // Never end on half a surrogate pair.
    if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
    {
      builder.Length--;
    }

    var sanitized = builder.ToString().TrimEnd();
    return sanitized.Length == 0 ? null : sanitized;
  }

  /// <summary>
  /// The body, or <see langword="null"/> when it is longer than <see cref="MaxBodyBytes"/> or
  /// cannot be read; the status then decides alone.
  /// </summary>
  private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(
    HttpResponseMessage response,
    CancellationToken cancellationToken
  )
  {
    if (response.Content.Headers.ContentLength > MaxBodyBytes)
    {
      return null;
    }

    try
    {
      await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
      var buffer = new byte[MaxBodyBytes + 1];
      var length = 0;
      while (length < buffer.Length)
      {
        var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
        if (read == 0)
        {
          return buffer.AsMemory(0, length);
        }

        length += read;
      }

      return null;
    }
    catch (Exception exception) when (exception is HttpRequestException or IOException)
    {
      return null;
    }
  }

  private static string? ReadString(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
      ? value.GetString()
      : null;
}

/// <summary>A renderer's error response, as far as the gateway reads it.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="IsProblem">Whether the body was declared <c>application/problem+json</c>.</param>
/// <param name="Kind">The problem's <c>kind</c>, unvalidated.</param>
/// <param name="Detail">The problem's <c>detail</c>, unsanitized.</param>
/// <param name="SandboxNotRunning">Whether this is the Sandboxes proxy's answer for a suspended sandbox.</param>
internal sealed record RendererFailure(
  int Status,
  bool IsProblem,
  string? Kind,
  string? Detail,
  bool SandboxNotRunning
);
