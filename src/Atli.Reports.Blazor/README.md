# Atli.Reports.Blazor

Generate PDF reports from Blazor components in .NET 10. Map a component to an HTTP endpoint, or
render it from your own code. Reports are rendered to HTML and converted to PDF by
[Atli.Reports.Engine](https://www.nuget.org/packages/Atli.Reports.Engine) in headless Chrome,
Chromium, or Edge.

Formerly **BlazorReports**. Coming from `BlazorReports`? See the
[migration guide](https://github.com/atlitech/reports/blob/main/docs/migration/from-blazorreports.md).
Part of [Atli Reports](https://github.com/atlitech/reports).

## Install

```bash
dotnet add package Atli.Reports.Blazor
```

## Map a report

`Program.cs` (in a project named `MyReports`):

```csharp
using Atli.Reports.Blazor.Extensions;
using MyReports;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddBlazorReports();

var app = builder.Build();

app.MapBlazorReport<HelloReport, HelloReportData>(); // POST /helloreport

app.Run();
```

`HelloReport.razor`, with `public record HelloReportData(string Name);` in the `MyReports`
namespace:

```razor
<h1>Hello, @Data.Name!</h1>

@code {
  [Parameter]
  public required HelloReportData Data { get; set; }
}
```

```bash
curl -X POST http://localhost:5000/helloreport \
  -H "Content-Type: application/json" \
  -d '{"name": "World"}' \
  --output hello.pdf
```

Failures before any of the report was sent answer with problem details: 503 when the engine is
busy or the browser is unavailable, 504 for timeouts, and 500 for rendering failures.

## Options

```csharp
builder.Services.AddBlazorReports(options =>
{
  options.BaseStylesPath = "wwwroot/styles/base.css"; // inlined into every report
  options.AssetsPath = "wwwroot/assets"; // data: URIs in BlazorReportsBase.GlobalAssets
  options.PageSettings.Orientation = BlazorReportsPageOrientation.Landscape;
});
```

`MapBlazorReport` takes per-report options too: `ReportName` (the route), `OutputFormat` (`Pdf`
or `Html`), `BaseStylesPath`, `AssetsPath`, `PageSettings`, and `JavaScriptSettings`.

## Wait for a report's JavaScript

```csharp
app.MapBlazorReport<SalesChart, SalesData>(options =>
{
  options.JavaScriptSettings.WaitForCompletedSignal = true;
  options.JavaScriptSettings.CompletedSignalTimeout = TimeSpan.FromSeconds(10);
});
```

The report calls `blazorReport.completed()` from its script when its charts or data are ready,
and the PDF is printed at that moment.

## Configure the engine

`AddBlazorReports` registers the engine. Configure it with `AddReportsEngine`, for example from
the `ReportsEngine` configuration section:

```csharp
builder.Services.AddReportsEngine(
  builder.Configuration.GetSection(ReportsEngineOptions.SectionName)
);
```

With the section bound, Linux containers usually need `ReportsEngine:Browser:NoSandbox` set to
`true` (trusted HTML only). For throughput, point `ReportsEngine:Browser:ExecutablePath` at
`chrome-headless-shell`.

## Learn more

- [Atli Reports README](https://github.com/atlitech/reports#readme): base styles, Tailwind CSS,
  assets, rendering from code, and benchmarks
- [Examples](https://github.com/atlitech/reports/tree/main/examples)
- [Migrating from BlazorReports](https://github.com/atlitech/reports/blob/main/docs/migration/from-blazorreports.md)
