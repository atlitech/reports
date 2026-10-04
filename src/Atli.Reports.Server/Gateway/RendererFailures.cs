using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Reads a renderer's error response, which may come from a compromised renderer: at most
/// <see cref="MaxBodyBytes"/> of it, and only the members the gateway needs. Nothing in it can make
/// the gateway throw: a body that is not JSON, not an object, or not valid text in a member counts
/// as that member missing.
/// </summary>
internal static class RendererFailures
{
  /// <summary>The most of an error body the gateway reads.</summary>
  public const int MaxBodyBytes = 16 * 1024;

  /// <summary>The longest renderer detail passed on to the caller.</summary>
  public const int MaxDetailLength = 512;

  /// <summary>
  /// What the Azure Container Apps Sandboxes proxy answers, with <c>403</c>, for a suspended
  /// sandbox. The renderer's own <c>403</c> is problem details, so the two cannot be confused; a
  /// compromised renderer can still send this, so the gateway checks the sandbox's state before it
  /// resumes it (see <see cref="SandboxWaker"/>).
  /// </summary>
  private const string SandboxNotRunning = "Sandbox is not running";

  /// <summary>
  /// What the proxy answers, with <c>404</c>, for a sandbox or port that does not exist, such as a
  /// renderer deleted since the gateway read its record.
  /// </summary>
  private const string SandboxNotFound = "Not found";

  /// <summary>
  /// The proxy's <c>errorCode</c>, with <c>403</c>, for a source address the port's allow-list does
  /// not admit: <c>{"error":"Access denied by IP access control policy","errorCode":"IpAccessDenied"}</c>.
  /// </summary>
  private const string AddressDenied = "IpAccessDenied";

  /// <summary>The kinds a renderer's problem may name, by name.</summary>
  private static readonly (string Name, ConversionErrorKind Kind)[] Kinds =
  [
    .. Enum.GetValues<ConversionErrorKind>()
      .Where(kind => kind != ConversionErrorKind.Canceled)
      .Select(kind => (kind.ToString(), kind)),
  ];

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
      return new RendererFailure(status, isProblem, null, null, ProxyAnswer.None);
    }

    JsonDocument document;
    try
    {
      document = JsonDocument.Parse(body.Value);
    }
    catch (JsonException)
    {
      return new RendererFailure(status, isProblem, null, null, ProxyAnswer.None);
    }

    using (document)
    {
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return new RendererFailure(status, isProblem, null, null, ProxyAnswer.None);
      }

      if (isProblem)
      {
        return new RendererFailure(
          status,
          true,
          ReadKind(root),
          ReadString(root, "detail"),
          ProxyAnswer.None
        );
      }

      var proxy = response.StatusCode switch
      {
        HttpStatusCode.Forbidden when StringEquals(root, "error", SandboxNotRunning) =>
          ProxyAnswer.SandboxNotRunning,
        HttpStatusCode.Forbidden when StringEquals(root, "errorCode", AddressDenied) =>
          ProxyAnswer.AddressDenied,
        HttpStatusCode.NotFound when StringEquals(root, "error", SandboxNotFound) =>
          ProxyAnswer.SandboxNotFound,
        _ => ProxyAnswer.None,
      };
      return new RendererFailure(status, false, null, null, proxy);
    }
  }

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

  /// <summary>
  /// The <see cref="ConversionErrorKind"/> a problem's <c>kind</c> names exactly, if it names one
  /// other than <see cref="ConversionErrorKind.Canceled"/>, which only the caller's own cancellation
  /// produces. Compared in place, so the renderer's string is never decoded.
  /// </summary>
  private static ConversionErrorKind? ReadKind(JsonElement problem)
  {
    foreach (var (name, kind) in Kinds)
    {
      if (StringEquals(problem, "kind", name))
      {
        return kind;
      }
    }

    return null;
  }

  /// <summary>
  /// The string member <paramref name="name"/>, or <see langword="null"/> when it is missing, not a
  /// string, or not valid text: a lone surrogate escape or invalid UTF-8 makes
  /// <see cref="JsonElement.GetString"/> throw <see cref="InvalidOperationException"/>, as an
  /// escaped member name can make the lookup itself.
  /// </summary>
  private static string? ReadString(JsonElement element, string name)
  {
    try
    {
      return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
    }
    catch (InvalidOperationException)
    {
      return null;
    }
  }

  /// <summary>
  /// Whether the string member <paramref name="name"/> is exactly <paramref name="expected"/>;
  /// <see langword="false"/> for text that is not valid, as in <see cref="ReadString"/>.
  /// </summary>
  private static bool StringEquals(JsonElement element, string name, string expected)
  {
    try
    {
      return element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.ValueEquals(expected);
    }
    catch (InvalidOperationException)
    {
      return false;
    }
  }
}

/// <summary>A renderer's error response, as far as the gateway reads it.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="IsProblem">Whether the body was declared <c>application/problem+json</c>.</param>
/// <param name="Kind">
/// The <see cref="ConversionErrorKind"/> a problem names, other than
/// <see cref="ConversionErrorKind.Canceled"/>; <see langword="null"/> for any other <c>kind</c>.
/// </param>
/// <param name="Detail">The problem's <c>detail</c>, unsanitized.</param>
/// <param name="Proxy">
/// Which of the Azure Container Apps Sandboxes proxy's own answers this is, if any. The renderer
/// answers every error with problem details, so these come from the platform, or from a
/// compromised renderer imitating it.
/// </param>
internal sealed record RendererFailure(
  int Status,
  bool IsProblem,
  ConversionErrorKind? Kind,
  string? Detail,
  ProxyAnswer Proxy
)
{
  /// <summary>Whether this is the Sandboxes proxy's answer for a suspended sandbox.</summary>
  public bool SandboxNotRunning => Proxy == ProxyAnswer.SandboxNotRunning;

  /// <summary>
  /// The renderer had no room for the conversion: its per-caller limit (<c>429</c>) or its own
  /// capacity (<c>503</c> with the kind <c>Busy</c>).
  /// </summary>
  public bool IsBusy =>
    Status == StatusCodes.Status429TooManyRequests
    || (Status == StatusCodes.Status503ServiceUnavailable && Kind == ConversionErrorKind.Busy);
}

/// <summary>The Azure Container Apps Sandboxes proxy's own answers that the gateway tells apart.</summary>
internal enum ProxyAnswer
{
  /// <summary>None of the answers below.</summary>
  None,

  /// <summary><c>403 {"error":"Sandbox is not running"}</c>: the sandbox is suspended.</summary>
  SandboxNotRunning,

  /// <summary>
  /// <c>404 {"error":"Not found"}</c>: no sandbox or port at the record's URL, such as a renderer
  /// deleted since the gateway read its record.
  /// </summary>
  SandboxNotFound,

  /// <summary>
  /// <c>403</c> with the <c>errorCode</c> <c>IpAccessDenied</c>: the port's allow-list does not
  /// admit the gateway's address.
  /// </summary>
  AddressDenied,
}
