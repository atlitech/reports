using System.Diagnostics.CodeAnalysis;
using Atli.Reports.Engine;
using Atli.Reports.Server.Models;
using Atli.Reports.Server.Security;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;

namespace Atli.Reports.Server.Endpoints;

/// <summary>
/// Maps <c>POST /convert</c>, which converts the posted HTML to a PDF.
/// </summary>
public static partial class ConvertEndpoints
{
  /// <summary>
  /// The longest signal wait the engine can time: .NET timers stop at 2^32 - 2 milliseconds, about
  /// 49.7 days. Anything longer, which <see cref="TimeSpan"/> may not even hold, is a bad request.
  /// </summary>
  private const double MaxWaitTimeoutSeconds = 4_294_967;

  /// <summary>
  /// Maps <c>POST /convert</c> and describes it to OpenAPI.
  /// </summary>
  /// <param name="app">The application.</param>
  public static void MapConvertEndpoints(this WebApplication app)
  {
    // The OpenAPI operation takes its summary, description, and response descriptions from the XML
    // comments of ConvertHtmlToPdf, which is internal because the generator that compiles them into
    // the binary skips private members. The responses are every status ConversionProblems gives a
    // written error, and the 413 and 415 of request binding; not the 499 of a canceled request,
    // which no client sees.
    app.MapPost("/convert", ConvertHtmlToPdf)
      .WithName("Convert")
      .RequireAuthorization(ReportsSecurityRegistration.ConvertPolicy)
      .WithMetadata(new ReportsSecurityMiddleware.ConversionAdmissionMetadata())
      .WithTags("Conversion")
      // Stream makes OpenAPI describe the PDF as binary content, not as a JSON object.
      .Produces<Stream>(StatusCodes.Status200OK, "application/pdf")
      .ProducesProblem(StatusCodes.Status400BadRequest)
      .ProducesProblem(StatusCodes.Status401Unauthorized)
      .ProducesProblem(StatusCodes.Status403Forbidden)
      .ProducesProblem(StatusCodes.Status429TooManyRequests)
      .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
      .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
      .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
      .ProducesProblem(StatusCodes.Status500InternalServerError)
      .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
      .ProducesProblem(StatusCodes.Status504GatewayTimeout)
      .AddOpenApiOperationTransformer(DescribeRetryAfter);
  }

  /// <summary>
  /// Adds the <c>Retry-After</c> header of the 503 response to the OpenAPI operation; XML comments
  /// cannot describe headers.
  /// </summary>
  private static Task DescribeRetryAfter(
    OpenApiOperation operation,
    OpenApiOperationTransformerContext context,
    CancellationToken cancellationToken
  )
  {
    if (operation.Responses?.GetValueOrDefault("503") is OpenApiResponse response)
    {
      response.Headers ??= new Dictionary<string, IOpenApiHeader>();
      response.Headers[HeaderNames.RetryAfter] = new OpenApiHeader
      {
        Description = "Seconds to wait before retrying: 1 for `Busy`, 5 for `BrowserUnavailable`.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
      };
    }

    if (operation.Responses?.GetValueOrDefault("429") is OpenApiResponse quotaResponse)
    {
      quotaResponse.Headers ??= new Dictionary<string, IOpenApiHeader>();
      quotaResponse.Headers[HeaderNames.RetryAfter] = new OpenApiHeader
      {
        Description = "Seconds to wait before retrying: 1 for per-caller capacity exhaustion.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
      };
    }

    return Task.CompletedTask;
  }

  /// <summary>
  /// Converts HTML to a PDF.
  /// </summary>
  /// <remarks>
  /// The PDF streams into the response as the browser produces it. A failure before its first byte
  /// is problem details with a <c>kind</c> member, the name of the error kind; a failure after it
  /// aborts the connection, so the client sees a broken response, never a truncated <c>200</c>.
  /// </remarks>
  /// <response code="200">The PDF, as an attachment named <c>output.pdf</c>.</response>
  /// <response code="400">
  /// <c>InvalidRequest</c>: the body is not a conversion request, the HTML is blank, or an option is
  /// invalid.
  /// </response>
  /// <response code="401"><c>Unauthorized</c>: credentials are missing or invalid.</response>
  /// <response code="403"><c>Forbidden</c>: the caller lacks conversion permission.</response>
  /// <response code="429"><c>Busy</c>: this caller has reached its in-flight limit.</response>
  /// <response code="413"><c>InvalidRequest</c>: the body is larger than the server accepts.</response>
  /// <response code="415"><c>InvalidRequest</c>: the body is not sent as <c>application/json</c>.</response>
  /// <response code="422"><c>SignalTimeout</c>: the document never called its signal function.
  /// <c>PolicyDenied</c>: the document attempted access prohibited by the rendering policy.</response>
  /// <response code="500">
  /// <c>RenderFailed</c>: the browser could not render or print the document, or the server failed
  /// unexpectedly.
  /// </response>
  /// <response code="503">
  /// <c>Busy</c>: the queue is full, or the wait for a turn timed out (<c>Retry-After: 1</c>).
  /// <c>BrowserUnavailable</c>: the browser is restarting or missing, or the server is shutting down
  /// (<c>Retry-After: 5</c>).
  /// </response>
  /// <response code="504">
  /// <c>Timeout</c>: a browser command, the page load, or the conversion as a whole took too long.
  /// </response>
  internal static async Task ConvertHtmlToPdf(
    HttpContext context,
    ConvertRequest request,
    IHtmlToPdfConverter converter,
    ILoggerFactory loggerFactory
  )
  {
    if (string.IsNullOrWhiteSpace(request.Html))
    {
      await ConversionProblems.WriteAsync(
        context,
        new ConversionError(ConversionErrorKind.InvalidRequest, "HTML content is required.")
      );
      return;
    }

    if (!TryMapOptions(request.Options, out var options, out var invalidOptions))
    {
      await ConversionProblems.WriteAsync(
        context,
        new ConversionError(ConversionErrorKind.InvalidRequest, invalidOptions)
      );
      return;
    }

    PdfResponseStream body = new(context.Response);
    var result = await converter.ConvertAsync(request.Html, body, options, context.RequestAborted);

    if (result.TryPickT0(out _, out var error))
    {
      body.Start();
      return;
    }

    if (body.HasStarted)
    {
      LogAbortedAfterStart(loggerFactory.CreateLogger("Atli.Reports.Server.Convert"), error.Kind);
      context.Abort();
      return;
    }

    await ConversionProblems.WriteAsync(context, error);
  }

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Warning,
    Message = "The conversion failed ({Kind}) after the PDF response started; aborting the connection."
  )]
  private static partial void LogAbortedAfterStart(ILogger logger, ConversionErrorKind kind);

  /// <summary>
  /// Maps the request options onto <see cref="PdfOptions"/>, or explains why they are invalid.
  /// </summary>
  private static bool TryMapOptions(
    PdfOptionsRequest? request,
    out PdfOptions options,
    [NotNullWhen(false)] out string? error
  )
  {
    options = new PdfOptions();
    error = null;
    if (request is null)
    {
      return true;
    }

    if (request.Orientation is not null)
    {
      PageOrientation? orientation = request.Orientation.ToLowerInvariant() switch
      {
        "portrait" => PageOrientation.Portrait,
        "landscape" => PageOrientation.Landscape,
        _ => null,
      };
      if (orientation is null)
      {
        error = $"Unknown orientation '{request.Orientation}'. Use portrait or landscape.";
        return false;
      }

      options.Orientation = orientation.Value;
    }

    if (request.PaperSize is not null)
    {
      PaperSize? paperSize = request.PaperSize.ToLowerInvariant() switch
      {
        "letter" => PaperSize.Letter,
        "legal" => PaperSize.Legal,
        "a4" => PaperSize.A4,
        "a3" => PaperSize.A3,
        _ => null,
      };
      if (paperSize is null)
      {
        error =
          $"Unknown paper size '{request.PaperSize}'. Use letter, legal, a4, or a3, or set paperWidth and paperHeight.";
        return false;
      }

      options.PaperSize = paperSize;
    }

    if (request.PaperWidth is not null || request.PaperHeight is not null)
    {
      if (request.PaperWidth is not > 0 || request.PaperHeight is not > 0)
      {
        error =
          "paperWidth and paperHeight must be set together, in inches, and be greater than zero.";
        return false;
      }

      options.PaperSize = new PaperSize
      {
        Width = request.PaperWidth.Value,
        Height = request.PaperHeight.Value,
      };
    }

    if (request.Margins is not null)
    {
      options.Margins = new Margins
      {
        Top = request.Margins.Top ?? 0.4,
        Bottom = request.Margins.Bottom ?? 0.4,
        Left = request.Margins.Left ?? 0.4,
        Right = request.Margins.Right ?? 0.4,
      };
    }

    if (request.PrintBackground.HasValue)
    {
      options.PrintBackground = request.PrintBackground.Value;
    }

    if (request.Scale.HasValue)
    {
      options.Scale = request.Scale.Value;
    }

    options.HeaderTemplate = request.HeaderTemplate;
    options.FooterTemplate = request.FooterTemplate;

    if (request.DisplayHeaderFooter.HasValue)
    {
      options.DisplayHeaderFooter = request.DisplayHeaderFooter.Value;
    }

    options.PageRanges = request.PageRanges;

    if (request.PreferCSSPageSize.HasValue)
    {
      options.PreferCssPageSize = request.PreferCSSPageSize.Value;
    }

    options.GenerateTaggedPdf = request.GenerateTaggedPdf;

    if (request.WaitForSignal is not null)
    {
      options.WaitForSignal = request.WaitForSignal;
    }

    if (request.WaitTimeoutSeconds is { } waitTimeoutSeconds)
    {
      // Negative values within range pass through: -0.001 is Timeout.InfiniteTimeSpan, and the engine
      // rejects any other negative wait when the request waits for a signal.
      if (
        !double.IsFinite(waitTimeoutSeconds)
        || Math.Abs(waitTimeoutSeconds) > MaxWaitTimeoutSeconds
      )
      {
        error =
          "waitTimeoutSeconds must be between 0 and 4294967 seconds (about 49 days), or -0.001 to wait until the request is canceled.";
        return false;
      }

      options.WaitTimeout = TimeSpan.FromSeconds(waitTimeoutSeconds);
    }

    return true;
  }
}
