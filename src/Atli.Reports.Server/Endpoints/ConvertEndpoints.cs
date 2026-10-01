using Atli.Reports.Engine;
using Atli.Reports.Server.Models;

namespace Atli.Reports.Server.Endpoints;

public static class ConvertEndpoints
{
  public static void MapConvertEndpoints(this WebApplication app)
  {
    app.MapPost("/convert", ConvertHtmlToPdf);
  }

  private static async Task<IResult> ConvertHtmlToPdf(
    ConvertRequest request,
    IHtmlToPdfConverter converter,
    CancellationToken ct
  )
  {
    if (string.IsNullOrWhiteSpace(request.Html))
    {
      return Results.BadRequest(new ErrorResponse { Error = "HTML content is required." });
    }

    var options = MapOptions(request.Options);
    var result = await converter.ConvertAsync(request.Html, options, ct);

    return result.Match<IResult>(
      stream => Results.File(stream, "application/pdf", "output.pdf"),
      error => Results.Problem(error.Message, statusCode: 500)
    );
  }

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
