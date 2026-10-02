# Atli.Reports.Blazor

Generate PDF reports from Blazor components in .NET 10. Map a component to an HTTP endpoint, or
render it from your own code. Reports are rendered to HTML and converted to PDF by
[Atli.Reports.Engine](https://www.nuget.org/packages/Atli.Reports.Engine) in headless Chrome,
Chromium, or Edge.

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
using Atli.Reports.Engine;

builder.Services.AddBlazorReports(options =>
{
  options.BaseStylesPath = "wwwroot/styles/base.css"; // inlined into every report
  options.AssetsPath = "wwwroot/assets"; // data: URIs in BlazorReportBase.GlobalAssets
  options.PdfOptions.Orientation = PageOrientation.Landscape;
  options.PdfOptions.PaperSize = PaperSize.A4;
});
```

`MapBlazorReport` takes per-report options too: `ReportName` (the route), `OutputFormat` (`Pdf`
or `Html`), `BaseStylesPath`, `AssetsPath`, `PdfOptions`, and `JavaScriptSettings`.

`PdfOptions` is the engine's type, including scale, header/footer templates, page ranges, CSS page
sizing, and PDF tagging. Each mapped or registered report starts with its own copy of the global
PDF options, so per-report changes do not affect other reports.

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

Enabling `WaitForCompletedSignal` overrides `PdfOptions.WaitForSignal` and `WaitTimeout` for that
conversion. Otherwise those engine options apply directly; when `WaitForSignal` is set, the
template also forwards `blazorReport.completed()` to that function.

## Configure the engine

`AddBlazorReports` registers the engine. Configure it with `AddReportsEngine`, for example from
the `ReportsEngine` configuration section:

```csharp
builder.Services.AddReportsEngine(
  builder.Configuration.GetSection(ReportsEngineOptions.SectionName)
);
```

In Linux containers, Chromium's sandbox needs the user namespaces that the seccomp profile
[`deploy/seccomp/chromium.json`](https://github.com/atlitech/reports/blob/main/deploy/seccomp/README.md)
allows; run the container with it. Where that is not possible, and only for trusted HTML, set
`ReportsEngine:Browser:NoSandbox` to `true` (with the section bound). For throughput, point
`ReportsEngine:Browser:ExecutablePath` at `chrome-headless-shell`.

## Convert on a reports server

To keep the browser out of the app, add
[Atli.Reports.Client](https://www.nuget.org/packages/Atli.Reports.Client) next to
`AddBlazorReports`, in either order:

```csharp
builder.Services.AddBlazorReports();
builder.AddReportsClient("reports"); // ConnectionStrings:reports = "Endpoint=http://reports:8080"
```

Components still render in the app; the HTML is converted by an
[Atli Reports server](https://github.com/atlitech/reports/blob/main/docs/engine/server.md), and
the app never starts a browser.

## Telemetry

Report generation is traced on the `Atli.Reports.Blazor` activity source: an
`atli.reports.blazor.generate` span per report, with an `atli.reports.blazor.render` child and,
for PDF output, the engine's conversion span. Add both sources to see the whole report:

```csharp
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
  .AddSource(BlazorReportTelemetry.ActivitySourceName)
  .AddSource(ReportsEngineTelemetry.ActivitySourceName));
```

## Learn more

- [Atli Reports README](https://github.com/atlitech/reports#readme): base styles, Tailwind CSS,
  assets, rendering from code, and benchmarks
- [Examples](https://github.com/atlitech/reports/tree/main/examples)
