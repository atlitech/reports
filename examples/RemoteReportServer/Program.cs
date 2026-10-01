using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Client;
using Atli.Reports.Engine;
using ExampleTemplates.Reports;
using RemoteReportServer;

var builder = WebApplication.CreateSlimBuilder(args);

builder.AddServiceDefaults();

// Blazor reports render in this app, and the reports server converts them to PDF. AddReportsClient
// reads ConnectionStrings:reports-server, which the AppHost sets with WithReference, and replaces the
// in-process engine's converter: this app never starts a browser and needs none installed. It also
// adds a health check, tagged ready, that probes the server, so /health reports the server too.
builder.Services.AddBlazorReports();
builder.AddReportsClient("reports-server");

var app = builder.Build();

app.MapDefaultEndpoints();

var reportsGroup = app.MapGroup("reports");

reportsGroup.MapBlazorReport<ReportWithRepeatingHeaderPerPage>();

// Any HTML document, through the same IHtmlToPdfConverter the reports use.
app.MapPost(
  "/html-to-pdf",
  async (
    HtmlDocument document,
    IHtmlToPdfConverter converter,
    CancellationToken cancellationToken
  ) =>
  {
    var result = await converter.ConvertAsync(
      document.Html,
      new PdfOptions { PaperSize = PaperSize.A4 },
      cancellationToken
    );

    return result.Match<IResult>(
      pdf => Results.File(pdf, "application/pdf", "document.pdf"),
      error =>
        Results.Problem(
          title: error.Message,
          statusCode: error.Kind switch
          {
            ConversionErrorKind.InvalidRequest => StatusCodes.Status400BadRequest,
            ConversionErrorKind.Busy or ConversionErrorKind.BrowserUnavailable =>
              StatusCodes.Status503ServiceUnavailable,
            ConversionErrorKind.Timeout or ConversionErrorKind.SignalTimeout =>
              StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError,
          }
        )
    );
  }
);

app.Run();
