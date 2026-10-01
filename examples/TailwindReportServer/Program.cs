using Atli.Reports.Blazor.Extensions;
using ExampleTemplates.ReportWithTailwind;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddBlazorReports(options =>
{
  options.BrowserOptions.DisableHeadless = true;
  options.BaseStylesPath = "wwwroot/styles/base.css";
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
  app.MapOpenApi();
}

var reportsGroup = app.MapGroup("reports");

reportsGroup.MapBlazorReport<ReportWithTailwind>();

app.Run();
