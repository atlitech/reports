using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Engine;
using ExampleTemplates.Reports;
using SimpleReportServer;

var builder = WebApplication.CreateSlimBuilder(args);

builder.AddServiceDefaults();

// Describes the report endpoints at /openapi/v1.json; ReportServer.http has requests to try them with.
builder.Services.AddOpenApi();
builder.Services.AddBlazorReports();

// Reads ReportsEngine:* from configuration, for example the browser path the AppHost can pass on.
builder.Services.AddReportsEngine(
  builder.Configuration.GetSection(ReportsEngineOptions.SectionName)
);
builder.Services.AddHealthChecks().AddReportsEngineBrowserCheck();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
  app.MapOpenApi();
}

var reportsGroup = app.MapGroup("reports");

reportsGroup.MapBlazorReport<HelloReport, HelloReportData>();
reportsGroup.MapBlazorReport<HelloReport, HelloReportData>(opts =>
{
  opts.ReportName = "HelloReportHtml";
  opts.OutputFormat = ReportOutputFormat.Html;
});
reportsGroup.MapBlazorReport<ReportWithRepeatingHeaderPerPage>(opts =>
{
  opts.OutputFormat = ReportOutputFormat.Pdf;
});

// Waits for the report's JavaScript to call blazorReport.completed() before printing the PDF.
reportsGroup.MapBlazorReport<AsyncJavaScriptReport, AsyncJavaScriptReportData>(opts =>
{
  opts.PdfOptions.WaitForSignal = "reportReady";
  opts.PdfOptions.WaitTimeout = TimeSpan.FromSeconds(10);
});

app.Run();
