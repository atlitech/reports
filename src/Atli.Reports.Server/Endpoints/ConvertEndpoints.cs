using Atli.Reports.Engine;
using Atli.Reports.Server.Models;

namespace Atli.Reports.Server.Endpoints;

public static partial class ConvertEndpoints
{
  public static void MapConvertEndpoints(this WebApplication app)
  {
    app.MapPost("/convert", ConvertHtmlToPdf);
  }

  /// <summary>
  /// Converts the posted HTML and streams the PDF into the response as the browser produces it.
  /// </summary>
  /// <remarks>
  /// The response stays uncommitted until the first PDF byte, so failures before that become problem
  /// details with the status from <see cref="ConversionProblems"/>. A failure after the first byte
  /// aborts the connection: the client sees a broken response, never a truncated 200.
  /// </remarks>
  private static async Task ConvertHtmlToPdf(
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

    var options = MapOptions(request.Options);
    PdfResponseStream body = new(context.Response);
    var result = await converter.ConvertAsync(request.Html, body, options, context.RequestAborted);

    if (result.TryPickT0(out _, out var error))
    {
      body.Start();
      return;
    }

    if (body.HasStarted)
    {
      LogAbortedAfterStart(
        loggerFactory.CreateLogger("Atli.Reports.Server.Convert"),
        error.Kind,
        error.Message
      );
      context.Abort();
      return;
    }

    await ConversionProblems.WriteAsync(context, error);
  }

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Warning,
    Message = "The conversion failed ({Kind}) after the PDF response started; aborting the connection: {Message}"
  )]
  private static partial void LogAbortedAfterStart(
    ILogger logger,
    ConversionErrorKind kind,
    string message
  );

  private static PdfOptions MapOptions(PdfOptionsRequest? request)
  {
    if (request is null)
    {
      return new PdfOptions();
    }

    PdfOptions options = new();

    if (request.Orientation is not null)
    {
      options.Orientation = request.Orientation.Equals(
        "landscape",
        StringComparison.OrdinalIgnoreCase
      )
        ? PageOrientation.Landscape
        : PageOrientation.Portrait;
    }

    if (request.PaperSize is not null)
    {
      options.PaperSize = request.PaperSize.ToLowerInvariant() switch
      {
        "a4" => PaperSize.A4,
        "a3" => PaperSize.A3,
        "legal" => PaperSize.Legal,
        _ => PaperSize.Letter,
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

    if (request.WaitForSignal is not null)
    {
      options.WaitForSignal = request.WaitForSignal;
    }

    if (request.WaitTimeoutSeconds.HasValue)
    {
      options.WaitTimeout = TimeSpan.FromSeconds(request.WaitTimeoutSeconds.Value);
    }

    return options;
  }
}
