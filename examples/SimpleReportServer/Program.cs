using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using ExampleTemplates.Reports;
using SimpleReportServer;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddBlazorReports();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
  app.UseSwagger();
  app.UseSwaggerUI();
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
  opts.JavaScriptSettings.WaitForCompletedSignal = true;
  opts.JavaScriptSettings.CompletedSignalTimeout = TimeSpan.FromSeconds(10);
});

app.Run();
