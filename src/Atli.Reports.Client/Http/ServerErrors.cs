using System.Globalization;
using System.Net;
using System.Text.Json;
using Atli.Reports.Engine;
using Polly.Timeout;

namespace Atli.Reports.Client.Http;

/// <summary>
/// Turns the server's error responses, and failures to reach it, into <see cref="ConversionError"/>s.
/// </summary>
internal static class ServerErrors
{
  /// <summary>
  /// The most of an error body the client reads to find its problem details.
  /// </summary>
  private const int MaxProblemBytes = 64 * 1024;

  /// <summary>
  /// Maps an error response. The problem details' <c>kind</c> member names the
  /// <see cref="ConversionErrorKind"/> exactly (which tells <c>Busy</c> from
  /// <c>BrowserUnavailable</c>, both <c>503</c>); without it, as from a proxy, the status decides.
  /// </summary>
  public static async Task<ConversionError> FromResponseAsync(
    HttpResponseMessage response,
    CancellationToken cancellationToken
  )
  {
    var status = (int)response.StatusCode;
    var problem = await ReadProblemAsync(response, cancellationToken);
    var kind = ParseKind(problem.Kind) ?? KindForStatus(response.StatusCode);
    var detail = problem.Detail ?? problem.Title ?? response.ReasonPhrase;
    var message = string.IsNullOrWhiteSpace(detail)
      ? string.Create(CultureInfo.InvariantCulture, $"The reports server answered {status}.")
      : string.Create(
        CultureInfo.InvariantCulture,
        $"The reports server answered {status}: {detail}"
      );
    return new ConversionError(kind, message);
  }

  /// <summary>
  /// A <c>200</c> that is not a PDF, so the endpoint is probably not a reports server.
  /// </summary>
  public static ConversionError NotAPdf(HttpResponseMessage response) =>
    new(
      ConversionErrorKind.RenderFailed,
      string.Create(
        CultureInfo.InvariantCulture,
        $"The reports server answered {(int)response.StatusCode} with '{response.Content.Headers.ContentType?.MediaType ?? "no content type"}' instead of a PDF. Check that the endpoint is a reports server."
      )
    );

  /// <summary>
  /// Whether <paramref name="exception"/> is a failure this class maps, rather than a bug to propagate.
  /// </summary>
  public static bool IsMapped(Exception exception) =>
    exception
      is HttpRequestException
        or IOException
        or OperationCanceledException
        or TimeoutRejectedException
        or ReportsAuthenticationException;

  /// <summary>
  /// Maps a failure to reach the server or to read its answer: cancellation by the caller is
  /// <see cref="ConversionErrorKind.Canceled"/>, a timeout is <see cref="ConversionErrorKind.Timeout"/>,
  /// and a connection or transport error is <see cref="ConversionErrorKind.BrowserUnavailable"/>:
  /// whatever renders the documents could not be reached, and a later attempt may succeed.
  /// </summary>
  public static ConversionError FromException(
    Exception exception,
    CancellationToken cancellationToken,
    string? context = null
  )
  {
    if (exception is ReportsAuthenticationException)
    {
      return new ConversionError(ConversionErrorKind.Unauthorized, exception.Message);
    }

    if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
    {
      return new ConversionError(
        ConversionErrorKind.Canceled,
        "The conversion was canceled.",
        exception
      );
    }

    // TimeoutRejectedException is the client's own attempt or total timeout; any other cancellation
    // the caller did not ask for is a timeout too, for example an HttpClient.Timeout set by the app.
    if (exception is TimeoutRejectedException or OperationCanceledException)
    {
      return new ConversionError(
        ConversionErrorKind.Timeout,
        "The reports server did not answer in time.",
        exception
      );
    }

    return new ConversionError(
      ConversionErrorKind.BrowserUnavailable,
      context ?? $"The reports server could not be reached: {exception.Message}",
      exception
    );
  }

  /// <summary>
  /// The kind for an error status that carries no problem <c>kind</c>.
  /// </summary>
  internal static ConversionErrorKind KindForStatus(HttpStatusCode status) =>
    (int)status switch
    {
      401 => ConversionErrorKind.Unauthorized,
      403 => ConversionErrorKind.Forbidden,
      422 => ConversionErrorKind.SignalTimeout,
      408 or 504 => ConversionErrorKind.Timeout,
      429 => ConversionErrorKind.Busy,
      499 => ConversionErrorKind.Canceled,
      502 or 503 => ConversionErrorKind.BrowserUnavailable,
      >= 400 and < 500 => ConversionErrorKind.InvalidRequest,
      _ => ConversionErrorKind.RenderFailed,
    };

  /// <summary>
  /// The <see cref="ConversionErrorKind"/> named by <paramref name="kind"/>, if it names one the server
  /// sends. <c>Canceled</c> never comes from the server: only the caller's own token cancels.
  /// </summary>
  private static ConversionErrorKind? ParseKind(string? kind) =>
    kind is { Length: > 0 }
    && char.IsLetter(kind[0])
    && Enum.TryParse<ConversionErrorKind>(kind, out var parsed)
    && Enum.IsDefined(parsed)
    && parsed != ConversionErrorKind.Canceled
      ? parsed
      : null;

  private static async Task<Problem> ReadProblemAsync(
    HttpResponseMessage response,
    CancellationToken cancellationToken
  )
  {
    var mediaType = response.Content.Headers.ContentType?.MediaType;
    if (mediaType is null || !mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase))
    {
      return default;
    }

    try
    {
      await response.Content.LoadIntoBufferAsync(MaxProblemBytes, cancellationToken);
      await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
      using var document = await JsonDocument.ParseAsync(
        body,
        cancellationToken: cancellationToken
      );
      if (document.RootElement.ValueKind != JsonValueKind.Object)
      {
        return default;
      }

      return new Problem(
        ReadString(document.RootElement, "kind"),
        ReadString(document.RootElement, "title"),
        ReadString(document.RootElement, "detail")
      );
    }
    catch (Exception exception)
      when (exception is JsonException or HttpRequestException or IOException
        && !cancellationToken.IsCancellationRequested
      )
    {
      // An unreadable or oversized body leaves the status to decide.
      return default;
    }
  }

  private static string? ReadString(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
      ? value.GetString()
      : null;

  private readonly record struct Problem(string? Kind, string? Title, string? Detail);
}
