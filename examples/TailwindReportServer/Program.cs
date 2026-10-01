using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Engine;
using ExampleTemplates.ReportWithTailwind;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddBlazorReports(options =>
{
  options.BaseStylesPath = "wwwroot/styles/base.css";
});

// Reads ReportsEngine:* from configuration, for example the browser path the AppHost can pass on.
builder.Services.AddReportsEngine(
  builder.Configuration.GetSection(ReportsEngineOptions.SectionName)
);
builder.Services.AddHealthChecks().AddReportsEngineBrowserCheck();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
  app.MapOpenApi();
}

var reportsGroup = app.MapGroup("reports");

reportsGroup.MapBlazorReport<ReportWithTailwind>();

app.Run();
