using Atli.Reports.Engine;
using Microsoft.AspNetCore.Diagnostics;

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
/// <see cref="ConversionErrorKind"/> name. Errors raised before the endpoint runs (a body that is not
/// a conversion request, a wrong content type, a body over the size limit) and unhandled exceptions
/// get the same shape, with the kind from <see cref="KindForStatus"/>.
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
  /// The kind for an error status that no <see cref="ConversionError"/> produced: the kind
  /// <c>Atli.Reports.Client</c> infers from that status alone, so naming it changes nothing for the
  /// client. A body the server cannot read (<c>400</c>, <c>413</c>, <c>415</c>) is
  /// <see cref="ConversionErrorKind.InvalidRequest"/>; an unhandled exception (<c>500</c>) is
  /// <see cref="ConversionErrorKind.RenderFailed"/>.
  /// </summary>
  public static ConversionErrorKind KindForStatus(int status) =>
    status switch
    {
      StatusCodes.Status422UnprocessableEntity => ConversionErrorKind.SignalTimeout,
      StatusCodes.Status408RequestTimeout or StatusCodes.Status504GatewayTimeout =>
        ConversionErrorKind.Timeout,
      StatusCodes.Status429TooManyRequests => ConversionErrorKind.Busy,
      Status499ClientClosedRequest => ConversionErrorKind.Canceled,
      StatusCodes.Status502BadGateway or StatusCodes.Status503ServiceUnavailable =>
        ConversionErrorKind.BrowserUnavailable,
      >= 400 and < 500 => ConversionErrorKind.InvalidRequest,
      _ => ConversionErrorKind.RenderFailed,
    };

  /// <summary>
  /// Completes every problem the server writes, including those from the exception handler and
  /// status code pages: adds the <c>kind</c> member when it is missing, and a detail for the request
  /// bodies the endpoint never got to see.
  /// </summary>
  internal static void Customize(ProblemDetailsContext context)
  {
    var problem = context.ProblemDetails;
    var status = problem.Status ?? context.HttpContext.Response.StatusCode;
    problem.Extensions.TryAdd("kind", KindForStatus(status).ToString());
    problem.Detail ??= status switch
    {
      StatusCodes.Status400BadRequest =>
        "The request body is not a conversion request. Send a JSON object with an html string and, optionally, options.",
      StatusCodes.Status413PayloadTooLarge => "The request body is larger than the server accepts.",
      StatusCodes.Status415UnsupportedMediaType =>
        "The request body must be JSON, sent with Content-Type: application/json.",
      _ => null,
    };
  }

  /// <summary>
  /// Writes <paramref name="error"/> as the response. Nothing is written for a canceled request.
  /// </summary>
  public static Task WriteAsync(HttpContext context, ConversionError error)
  {
    var status = StatusCode(error.Kind);
    if (error.Kind == ConversionErrorKind.Canceled)
    {
      context.Response.StatusCode = status;
      // Status code pages would otherwise write a problem body for the bare 499.
      if (context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
      {
        statusCodePages.Enabled = false;
      }

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
