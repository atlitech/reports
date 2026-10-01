using System.Diagnostics.CodeAnalysis;
using Atli.Reports.Engine;
using Atli.Reports.Server.Models;

namespace Atli.Reports.Server.Endpoints;

public static partial class ConvertEndpoints
{
  /// <summary>
  /// The longest signal wait the engine can time: .NET timers stop at 2^32 - 2 milliseconds, about
  /// 49.7 days. Anything longer, which <see cref="TimeSpan"/> may not even hold, is a bad request.
  /// </summary>
  private const double MaxWaitTimeoutSeconds = 4_294_967;

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
