using Atli.Reports.Engine;

namespace Atli.Reports.Server.Endpoints;

/// <summary>
/// Maps a <see cref="ConversionError"/> onto an HTTP response.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Kind</term><description>Status</description></listheader>
/// <item><term><see cref="ConversionErrorKind.InvalidRequest"/></term><description>400 Bad Request</description></item>
/// <item><term><see cref="ConversionErrorKind.SignalTimeout"/></term><description>422 Unprocessable Content: the document never signaled that it was ready.</description></item>
/// <item><term><see cref="ConversionErrorKind.Busy"/></term><description>503 Service Unavailable, <c>Retry-After: 1</c></description></item>
/// <item><term><see cref="ConversionErrorKind.BrowserUnavailable"/></term><description>503 Service Unavailable, <c>Retry-After: 5</c></description></item>
/// <item><term><see cref="ConversionErrorKind.Timeout"/></term><description>504 Gateway Timeout</description></item>
/// <item><term><see cref="ConversionErrorKind.Canceled"/></term><description>499 Client Closed Request, with no body: the client is gone.</description></item>
/// <item><term><see cref="ConversionErrorKind.RenderFailed"/></term><description>500 Internal Server Error</description></item>
/// </list>
/// Error bodies are RFC 9457 problem details with an extra <c>kind</c> member holding the
/// <see cref="ConversionErrorKind"/> name.
/// </remarks>
public static class ConversionProblems
{
  /// <summary>
  /// The status for a request the client abandoned (nginx convention). Never reaches the client.
  /// </summary>
  public const int Status499ClientClosedRequest = 499;

  /// <summary>
  /// The HTTP status for <paramref name="kind"/>.
  /// </summary>
  public static int StatusCode(ConversionErrorKind kind) =>
    kind switch
    {
      ConversionErrorKind.InvalidRequest => StatusCodes.Status400BadRequest,
      ConversionErrorKind.SignalTimeout => StatusCodes.Status422UnprocessableEntity,
      ConversionErrorKind.Busy => StatusCodes.Status503ServiceUnavailable,
      ConversionErrorKind.BrowserUnavailable => StatusCodes.Status503ServiceUnavailable,
      ConversionErrorKind.Timeout => StatusCodes.Status504GatewayTimeout,
      ConversionErrorKind.Canceled => Status499ClientClosedRequest,
      _ => StatusCodes.Status500InternalServerError,
    };

  /// <summary>
  /// The <c>Retry-After</c> value, in seconds, for <paramref name="kind"/>, or <see langword="null"/>
  /// when retrying is not expected to help.
  /// </summary>
  public static int? RetryAfterSeconds(ConversionErrorKind kind) =>
    kind switch
    {
      ConversionErrorKind.Busy => 1,
      ConversionErrorKind.BrowserUnavailable => 5,
      _ => null,
    };

  /// <summary>
  /// Writes <paramref name="error"/> as the response. Nothing is written for a canceled request.
  /// </summary>
  public static Task WriteAsync(HttpContext context, ConversionError error)
  {
    var status = StatusCode(error.Kind);
    if (error.Kind == ConversionErrorKind.Canceled)
    {
      context.Response.StatusCode = status;
      return Task.CompletedTask;
    }

    if (RetryAfterSeconds(error.Kind) is { } retryAfter)
    {
      context.Response.Headers.RetryAfter = retryAfter.ToString(
        System.Globalization.CultureInfo.InvariantCulture
      );
    }

    return Results
      .Problem(
        detail: error.Message,
        statusCode: status,
        title: Title(error.Kind),
        extensions: new Dictionary<string, object?> { ["kind"] = error.Kind.ToString() }
      )
      .ExecuteAsync(context);
  }

  private static string Title(ConversionErrorKind kind) =>
    kind switch
    {
      ConversionErrorKind.InvalidRequest => "The conversion request is invalid.",
      ConversionErrorKind.SignalTimeout => "The document did not signal that it was ready.",
      ConversionErrorKind.Busy => "The server is busy. Try again later.",
      ConversionErrorKind.BrowserUnavailable =>
        "The browser that renders documents is unavailable.",
      ConversionErrorKind.Timeout => "The conversion did not finish in time.",
      _ => "The document could not be converted.",
    };
}
