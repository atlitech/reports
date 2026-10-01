# Atli Reports

Open-source PDF generation for .NET. Atli Reports renders HTML to PDF in a headless Chromium
browser over the Chrome DevTools Protocol, and turns Blazor components into PDF reports. It was
formerly **BlazorReports**.

> [!IMPORTANT]
> The `Atli.Reports.*` packages are **not on NuGet yet**. They first ship with version 0.26.0,
> which has no release date yet. Until then, the published package is
> [BlazorReports 0.25.1](https://www.nuget.org/packages/BlazorReports/0.25.1), documented in its
> [0.25.1 README](https://github.com/atlitech/reports/blob/0.25.1/README.md). Once 0.26.0 ships,
> `BlazorReports` and `BlazorReports.Components` will be deprecated in favor of
> `Atli.Reports.Blazor`.

## What's inside

| Component | What it does | How you get it |
| --- | --- | --- |
| [`Atli.Reports.Engine`](src/Atli.Reports.Engine) | Converts HTML to PDF inside any .NET 10 app. One long-lived browser serves all conversions, each in a browser context of its own. Concurrency is bounded and the rest wait in a FIFO queue. The PDF streams out as the browser produces it. Also: JavaScript completion signals, crash recovery, metrics, and health checks. NativeAOT compatible. | NuGet package, from 0.26.0 |
| [`Atli.Reports.Blazor`](src/Atli.Reports.Blazor) and [`Atli.Reports.Blazor.Components`](src/Atli.Reports.Blazor.Components) | Turns Blazor components into PDF (or HTML) reports. Map a component to an HTTP endpoint, or render it from your own code. Runs on the engine. | NuGet packages, from 0.26.0 |
| [`Atli.Reports.Server`](src/Atli.Reports.Server) | A NativeAOT HTTP service over the engine: `POST /convert` with HTML and options, get a PDF back. Ships as a container image with `chrome-headless-shell`. | Build the image from this repository; no published image yet |
| [`Atli.Reports.Aspire.Hosting`](src/Atli.Reports.Aspire.Hosting) | Runs the server in an [Aspire](https://aspire.dev) AppHost: `builder.AddReportsServer("reports")`, with its health check, telemetry, typed settings, and the connection string the client reads. See [docs/aspire.md](docs/aspire.md). | NuGet package, from 0.26.0 |

The packages target .NET 10, so an AppHost that uses `Atli.Reports.Aspire.Hosting` targets .NET 10
too. Every component needs a Chromium-based browser; see
[Browser requirements](#browser-requirements).

## Quick start: a Blazor report endpoint

Add the package to an ASP.NET Core project (named `MyReports` here):

```bash
dotnet add package Atli.Reports.Blazor   # available from 0.26.0
```

`Program.cs`:

```csharp
using Atli.Reports.Blazor.Extensions;
using MyReports;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddBlazorReports();

var app = builder.Build();

app.MapBlazorReport<HelloReport, HelloReportData>(); // POST /helloreport

app.Run();
```

`HelloReportData.cs`:

```csharp
namespace MyReports;

public record HelloReportData(string Name);
```

`HelloReport.razor`:

```razor
<h1>Hello, @Data.Name!</h1>

@code {
  [Parameter]
  public required HelloReportData Data { get; set; }
}
```

Run the app and post the report's data as JSON (use the URL `dotnet run` prints):

```bash
curl -X POST http://localhost:5000/helloreport \
  -H "Content-Type: application/json" \
  -d '{"name": "World"}' \
  --output hello.pdf
```

The route is the component's name in lower case. `MapBlazorReport<MyReport>()` maps a report
without data. More in [Blazor reports](#blazor-reports) below.

## Quick start: the engine in a console app or worker

```bash
dotnet add package Atli.Reports.Engine   # available from 0.26.0
```

```csharp
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

ServiceCollection services = new();
services.AddReportsEngine();

await using var provider = services.BuildServiceProvider();
var converter = provider.GetRequiredService<IHtmlToPdfConverter>();

await using var file = File.Create("hello.pdf");
var result = await converter.ConvertAsync(
  "<!DOCTYPE html><h1>Hello, PDF</h1>",
  file,
  new PdfOptions { PaperSize = PaperSize.A4 }
);

result.Switch(
  _ => Console.WriteLine("Wrote hello.pdf"),
  error => Console.Error.WriteLine($"{error.Kind}: {error.Message}")
);
```

`IHtmlToPdfConverter` is a thread-safe singleton with two overloads. One streams the PDF into a
`Stream` you pass in, as above. The other returns the PDF as a seekable in-memory `Stream` that
you dispose. Failures come back as a `ConversionError` instead of an exception. Its `Kind` is one
of `InvalidRequest`, `Busy`, `BrowserUnavailable`, `Timeout`, `SignalTimeout`, `RenderFailed`, or
`Canceled`.

The browser starts on the first conversion. It closes when the service provider is disposed, or
when the host stops. In a generic host or ASP.NET Core app, register the engine with
`builder.Services.AddReportsEngine(...)` and inject `IHtmlToPdfConverter`. The host then drains
running conversions on shutdown, and can start the browser early (`Browser:WarmUpOnStartup`).

## Quick start: the server in Docker

The image builds from the repository root:

```bash
git clone https://github.com/atlitech/reports.git
cd reports
docker build -f src/Atli.Reports.Server/Dockerfile -t atli-reports-server .
docker run --rm -p 8080:8080 atli-reports-server
```

`docker compose up --build` in `src/Atli.Reports.Server` does the same. Then convert a document:

```bash
curl -X POST http://localhost:8080/convert \
  -H "Content-Type: application/json" \
  -d '{"html": "<!DOCTYPE html><h1>Hello, PDF</h1>", "options": {"paperSize": "a4"}}' \
  --output hello.pdf
```

The options mirror `PdfOptions`: `orientation`, `paperSize` (`letter`, `a4`, `a3`, `legal`),
`margins`, `printBackground`, `scale`, `headerTemplate`, `footerTemplate`, `displayHeaderFooter`,
`pageRanges`, `preferCSSPageSize`, `waitForSignal`, and `waitTimeoutSeconds`. Failures are
RFC 9457 problem details with a `kind` member. `GET /health/live` and `GET /health/ready` serve
container probes. The image runs as a non-root user under `tini`, and its browser runs without
the sandbox, so send it trusted HTML only. See [docs/engine/server.md](docs/engine/server.md) for
status codes, configuration, and the image.

## Waiting for JavaScript

By default, the engine prints once the document has fired its `load` event and its web fonts
are ready. A page that renders asynchronously can call a completion signal instead:

```csharp
var result = await converter.ConvertAsync(
  html,
  new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TimeSpan.FromSeconds(10) }
);
```

```html
<script>
  drawCharts().then(() => window.pdfReady());
</script>
```

The engine defines `window.pdfReady()` before any of the page's scripts run, without modifying
the HTML, and prints at the moment it is called. A page that does not call it within
`WaitTimeout` (default 30 seconds) fails with `ConversionErrorKind.SignalTimeout`.

- **Server:** send `"options": {"waitForSignal": "pdfReady", "waitTimeoutSeconds": 10}`. A missing
  signal is `422 Unprocessable Content`.
- **Blazor:** set `JavaScriptSettings.WaitForCompletedSignal = true` (see
  [below](#waiting-for-a-reports-javascript)), and call `blazorReport.completed()` from the report.

The design note [docs/engine/reactive-signal-approach.md](docs/engine/reactive-signal-approach.md)
explains how the signal works.

## Configuration

The engine reads `ReportsEngineOptions`. Set them in code:

```csharp
services.AddReportsEngine(options =>
{
  options.Browser.ExecutablePath = "/opt/chrome-headless-shell/chrome-headless-shell";
  options.Browser.WarmUpOnStartup = true;
  options.Concurrency.MaxConcurrentConversions = 4;
  options.ConversionTimeout = TimeSpan.FromSeconds(60);
});
```

Or bind them from the `ReportsEngine` configuration section (binding uses the source generator,
so it is safe in trimmed and NativeAOT apps):

```csharp
builder.Services.AddReportsEngine(
  builder.Configuration.GetSection(ReportsEngineOptions.SectionName)
);
```

```json
{
  "ReportsEngine": {
    "Browser": { "WarmUpOnStartup": true },
    "Concurrency": { "MaxConcurrentConversions": 4, "MaxQueueLength": 50 },
    "ConversionTimeout": "00:01:00"
  }
}
```

Configuration is only read when you bind it this way; `AddReportsEngine()` alone, and
`AddBlazorReports()`, use the defaults plus whatever you set in code. Once the section is bound,
environment variables work as usual, with `__` as the separator, for example
`ReportsEngine__Concurrency__MaxConcurrentConversions=4`. The keys below are relative to the
section, and also name the properties to set in code. The most used ones:

| Key | Default | Meaning |
| --- | --- | --- |
| `Browser:ExecutablePath` | found automatically | The browser executable; `chrome-headless-shell` is recommended |
| `Browser:Kind` | `Chrome` | The browser to look for when no path is set (`Chrome` or `Edge`) |
| `Browser:NoSandbox` | `false` | Disable Chromium's sandbox; only for trusted HTML |
| `Browser:DisableDevShmUsage` | `false` | Use the temporary directory instead of a small `/dev/shm` |
| `Browser:WarmUpOnStartup` | `false` | Start the browser with the host instead of on the first conversion |
| `Browser:CommandTimeout` | `00:00:30` | Time per DevTools command; also bounds the load wait and printing |
| `Browser:IdleTimeout` | infinite | Close the browser after this long without conversions |
| `Browser:MaxConversionsPerProcess` | `1000` | Replace the browser after this many conversions; `0` never |
| `Browser:MaxProcessLifetime` | `01:00:00` | Replace the browser after this long |
| `Concurrency:MaxConcurrentConversions` | processor count, 2 to 8 | Conversions rendering at once |
| `Concurrency:MaxQueueLength` | `100` | Conversions waiting for a turn; beyond that, `Busy` |
| `Concurrency:QueueTimeout` | `00:00:30` | Longest wait for a turn before `Busy` |
| `ConversionTimeout` | infinite | Longest a whole conversion may take, queue wait included |

The [configuration reference](docs/engine/architecture.md#configuration-reference) lists every
key. Invalid values fail with an `OptionsValidationException` that lists every problem, at host
start-up or when the engine is first used.

Per-conversion settings live on `PdfOptions`. The defaults print a portrait US Letter page with
0.4 inch margins and backgrounds. You can set `Orientation`, `PaperSize` (`Letter`, `Legal`, `A4`,
`A3`, or a custom size in inches), `Margins`, `PrintBackground`, `Scale`, `HeaderTemplate`,
`FooterTemplate`, `DisplayHeaderFooter`, `PageRanges`, `PreferCssPageSize`, `GenerateTaggedPdf`,
`WaitForSignal`, and `WaitTimeout`.

**Health checks and metrics.** `AddHealthChecks().AddReportsEngineBrowserCheck()` reports
unhealthy while the browser cannot start; the engine keeps retrying the launch in the background
and the check turns healthy once it succeeds. `.AddReportsEngineConversionCheck()` reports unhealthy when most
recent conversions failed. The engine publishes metrics on the `Atli.Reports.Engine` meter:
conversion duration and outcome, active conversions, queue length and wait, and browser
launches, crashes, and recycles. See
[architecture.md](docs/engine/architecture.md#metrics) for the instrument names.

## Browser requirements

- **Which browser.** Google Chrome or Chromium (`Browser:Kind = Chrome`, the default), or
  Microsoft Edge (`Edge`). The engine looks in the standard install locations on Windows, macOS,
  and Linux. Set `Browser:ExecutablePath` to use any other Chromium-based executable.
- **For throughput, use `chrome-headless-shell`.** It is Chromium's dedicated headless build,
  downloadable from [Chrome for Testing](https://googlechromelabs.github.io/chrome-for-testing/).
  Point `Browser:ExecutablePath` at it; the engine does not find it automatically. In the full
  desktop browser, creating each conversion's browser context is expensive, which caps
  throughput at about 7 conversions a second however many cores there are. `chrome-headless-shell`
  does the same work in a few milliseconds and has no such cap (see
  [the measurements](docs/engine/architecture.md#isolation)). The server image ships it.
- **Linux containers.** Chromium's sandbox needs user namespaces, which containers and some hosts
  block (for example Ubuntu 24.04 with its default AppArmor policy). Set `Browser:NoSandbox=true`,
  which suits trusted HTML only, or use the provided server image, which already does. Set
  `Browser:DisableDevShmUsage=true` where `/dev/shm` is small, as in Docker's 64 MB default.
  Install the fonts your documents use; the server image installs Liberation and Noto Color
  Emoji.

## Performance

We compared `Atli.Reports.Server` (engine commit 3829bfd, `chrome-headless-shell`) with
[Gotenberg](https://gotenberg.dev) 8.37.0 (Chromium route). Each ran in its own container limited
to 2 CPUs and 2 GB, one at a time, with closed-loop clients sending the same documents. The host
was an Apple M1 Max running Docker in OrbStack, and it was not fully idle. *docs/s* is valid PDFs
per second; latencies are the 95th percentile.

| Document | Clients | Atli docs/s | Gotenberg docs/s | Atli p95 | Gotenberg p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Invoice, 1 page | 1 | 40.5 | 10.7 | 45 ms | 103 ms |
| Invoice, 1 page | 16 | 37.0 | 12.3 | 485 ms | 1,870 ms |
| JavaScript chart with a completion signal | 16 | 33.5 | 9.2 | 522 ms | 4,484 ms |
| 1.5 MB page with 11 inlined images | 16 | 16.5 | 7.2 | 1,030 ms | 2,684 ms |
| 49-page table | 4 | 1.28 | 0.46 (10 of 25 requests failed) | 3,194 ms | 6,383 ms |
| 49-page table | 16 | 1.28 | 0 (every request failed) | 12,796 ms | — |

Atli used 3 to 4 times less CPU per document on the invoice and chart documents (invoice: 0.05 s
against 0.16 to 0.19 s), and about 2.5 times less on the image-heavy page. At 64 clients, 26 of
102 Atli requests for the 49-page table missed the 30 second client deadline. Gotenberg was not
measured at that level after failing at 16.

These numbers come from one run on one machine, so read them as indicative. The
[full results](benchmarks/results/2026-10-01-3829bfd.md) include every concurrency level, memory,
errors, and the environment. The [methodology](benchmarks/README.md#methodology) explains how to
reproduce them with `benchmarks/run.sh`. They measure the server image with
`chrome-headless-shell`. In-process with a full desktop Chrome, throughput tops out at about 7
conversions a second (see [Browser requirements](#browser-requirements)).

## Blazor reports

### Base styles

Point `BaseStylesPath` at a CSS file. Its content is inlined into every report:

```csharp
builder.Services.AddBlazorReports(options =>
{
  options.BaseStylesPath = "wwwroot/styles/base.css";
});
```

### Tailwind CSS 4

1. Install Tailwind at the root of your repository:
   ```bash
   bun add tailwindcss @tailwindcss/cli
   ```
2. Add `wwwroot/tailwindcss/input.css` to your project:
   ```css
   @import "tailwindcss";
   ```
3. Generate `base.css` once, or on every change:
   ```bash
   bunx @tailwindcss/cli -i ./path_to_your_project/wwwroot/tailwindcss/input.css -o ./path_to_your_project/wwwroot/styles/base.css -m
   bunx @tailwindcss/cli -i ./path_to_your_project/wwwroot/tailwindcss/input.css -o ./path_to_your_project/wwwroot/styles/base.css -m --watch
   ```
4. Set `BaseStylesPath` to `wwwroot/styles/base.css` as above.

[`examples/TailwindReportServer`](examples/TailwindReportServer) is a working setup.

### Assets

Files in `AssetsPath` reach reports that inherit `BlazorReportsBase` as `data:` URIs in the
`GlobalAssets` dictionary, keyed by file name:

```csharp
builder.Services.AddBlazorReports(options =>
{
  options.AssetsPath = "wwwroot/assets";
});
```

```razor
@inherits Atli.Reports.Blazor.Components.BlazorReportsBase

<img src="@GlobalAssets.GetValueOrDefault("logo.png")" alt="Logo" />
```

Styles and assets are read once, when the reports are registered; restart the app to pick up
changes to them.

### Page settings and per-report options

`options.PageSettings` sets the default orientation, margins, paper size (in inches), and
background printing. Each mapped report can override the defaults, and choose its route and
output format:

```csharp
app.MapBlazorReport<HelloReport, HelloReportData>(options =>
{
  options.ReportName = "greeting"; // POST /greeting
  options.OutputFormat = ReportOutputFormat.Html;
  options.BaseStylesPath = "wwwroot/styles/greeting.css";
});
```

HTML output skips the browser entirely.

### Waiting for a report's JavaScript

A report that renders in JavaScript (a chart library, data fetched in the page) can ask for the
PDF to be printed only once that work is done:

```csharp
app.MapBlazorReport<SalesChart, SalesData>(options =>
{
  options.JavaScriptSettings.WaitForCompletedSignal = true;
  options.JavaScriptSettings.CompletedSignalTimeout = TimeSpan.FromSeconds(10);
});
```

```razor
<div id="chart"></div>

<script>
  drawChart(document.getElementById("chart")).then(function () {
    blazorReport.completed();
  });
</script>
```

Set the same properties on `AddBlazorReports(options => options.JavaScriptSettings...)` to make
them the default for every report. A report that does not call `blazorReport.completed()` within
`CompletedSignalTimeout` (default 30 seconds) fails with `504 Gateway Timeout`.
[`examples/SimpleReportServer`](examples/SimpleReportServer) includes such a report.

### Errors, OpenAPI, and authorization

When a report fails before any of it was sent, the endpoint answers with problem details. The
status depends on the cause: 400 for an invalid request, 503 when the engine is busy or the
browser is unavailable, 504 for timeouts, and 500 for rendering failures. A failure after part
of the PDF was sent aborts the response, so a client never mistakes a truncated PDF for a
complete one.

`MapBlazorReport` returns a `RouteHandlerBuilder`, so report endpoints work like any other
Minimal API endpoint: add `.RequireAuthorization()`, filters, or OpenAPI metadata. Endpoints
describe their request body and responses to OpenAPI (`builder.Services.AddOpenApi()` and
`app.MapOpenApi()`).

### Rendering a report from your own code

Register the report without an endpoint, and generate it through `IReportService`:

```csharp
app.RegisterBlazorReport<HelloReport, HelloReportData>();
```

```csharp
var report = reportService.GetReportByName("HelloReport")!;

await using var file = File.Create("hello.pdf");
var result = await reportService.GenerateReport(file, report, new HelloReportData("World"));

if (result.TryPickT1(out var error, out _))
{
  Console.Error.WriteLine($"{error.Kind}: {error.Message}");
}
```

### Configuring the engine

`AddBlazorReports` registers the engine. To configure it, also call `AddReportsEngine`, before or
after `AddBlazorReports`:

```csharp
builder.Services.AddBlazorReports();
builder.Services.AddReportsEngine(
  builder.Configuration.GetSection(ReportsEngineOptions.SectionName)
);
```

## Migrating from BlazorReports

Atli.Reports.Blazor keeps BlazorReports' type and method names. Moving over takes three steps:
swap the packages, replace `BlazorReports.` with `Atli.Reports.Blazor.` in `using`, `@using`, and
`@inherits` directives, and target .NET 10. Some behavior changed, such as endpoint status codes
and obsolete `IReportService` overloads. The
[migration guide](docs/migration/from-blazorreports.md) covers every change.

## Documentation and examples

- [Engine architecture](docs/engine/architecture.md): browser lifecycle, isolation, concurrency,
  streaming, metrics, and the configuration reference
- [Server](docs/engine/server.md): the HTTP API, status codes, and the container image
- [Aspire](docs/aspire.md): run the server from an AppHost and convert from your apps through
  `Atli.Reports.Client`, locally and deployed
- [JavaScript completion signals](docs/engine/reactive-signal-approach.md)
- [Migrating from BlazorReports](docs/migration/from-blazorreports.md)
- [Benchmarks](benchmarks/README.md)
- [`examples/SimpleReportServer`](examples/SimpleReportServer): reports with and without data,
  HTML output, and a report that waits for its JavaScript
- [`examples/RemoteReportServer`](examples/RemoteReportServer): an app with no browser of its own. It
  renders Blazor reports and converts any HTML on the reports server through `Atli.Reports.Client`;
  the reference consumer for [Aspire](docs/aspire.md)
- [`examples/TailwindReportServer`](examples/TailwindReportServer): a report styled with Tailwind
  CSS 4
- [`examples/ExampleTemplates`](examples/ExampleTemplates): shared report components, including a
  header that repeats on every page

## Run it locally

An [Aspire](https://aspire.dev) AppHost, [`examples/Atli.Reports.AppHost`](examples/Atli.Reports.AppHost),
runs the server and the examples together, with their logs, traces, and metrics (the engine's
included) in the Aspire dashboard. You need the .NET 10 SDK, the
[Aspire CLI](https://aspire.dev/get-started/install-cli/), Docker, Chrome or Chromium, and
[Bun](https://bun.sh) for the Tailwind example. From the repository root:

```bash
aspire start   # builds and starts everything in the background, and prints the dashboard URL
aspire stop
```

| Resource | What it is |
| --- | --- |
| `reports-server` | `Atli.Reports.Server` in a container built from its Dockerfile; `POST /convert` as in [the server quick start](#quick-start-the-server-in-docker). The first build compiles the NativeAOT server and downloads `chrome-headless-shell`, so it takes a few minutes. |
| `remote-report-server` | [`examples/RemoteReportServer`](examples/RemoteReportServer), which converts on `reports-server` and starts no browser; `POST /reports/reportwithrepeatingheaderperpage` and `POST /html-to-pdf` with `{"html": "..."}` |
| `simple-report-server` | [`examples/SimpleReportServer`](examples/SimpleReportServer); the dashboard links its Swagger UI |
| `tailwind-report-server` | [`examples/TailwindReportServer`](examples/TailwindReportServer); `POST /reports/reportwithtailwind` |
| `tailwind-css` | Generates the Tailwind example's stylesheet (`bun install`, then the Tailwind CLI) and exits |
| `gotenberg` | [Gotenberg](https://gotenberg.dev) 8.37 with Chromium, for side-by-side comparisons. Off unless you start with `aspire start -- --Gotenberg:Enabled=true` |

Ports are assigned when the AppHost starts; the dashboard and `aspire describe` list each
resource's URLs. The examples run the engine in-process with the Chrome or Chromium installed on
your machine. To use another browser, such as `chrome-headless-shell`, pass its path:
`aspire start -- --ReportsEngine:Browser:ExecutablePath=/path/to/chrome-headless-shell`. Both
settings can also live in the AppHost's `appsettings.json` or user secrets.

### End-to-end tests

[`tests/Atli.Reports.AppHost.Tests`](tests/Atli.Reports.AppHost.Tests) starts the same AppHost with
`Aspire.Hosting.Testing`, waits for the resources to turn healthy, and checks that the server, the
remote example, and the in-process examples each return a PDF, and that the remote example starts no
browser. It needs what `aspire start` needs except the Aspire CLI: without one, building the AppHost
fetches the CLI release that matches its SDK with `dnx`, for the orchestrator. The solution leaves the
project out of its build, so a plain `dotnet test` skips it. From the repository root:

```bash
dotnet test --project tests/Atli.Reports.AppHost.Tests
```

The first run builds the server image and takes a few minutes. Each resource's console output goes
to `artifacts/bin/Atli.Reports.AppHost.Tests/debug/TestResults/resource-logs`. CI runs the tests in
[`aspire-e2e.yml`](.github/workflows/aspire-e2e.yml).

## History

The project started as **BlazorReports**, which turned Blazor components into PDF reports and was
published on NuGet as `BlazorReports` and `BlazorReports.Components` up to version 0.25.1. It is
now **Atli Reports**. The Blazor packages are renamed `Atli.Reports.Blazor` and
`Atli.Reports.Blazor.Components`. They run on `Atli.Reports.Engine`, a standalone HTML-to-PDF
engine that also powers `Atli.Reports.Server`.

## Contributing

Issues and pull requests are welcome at
[github.com/atlitech/reports](https://github.com/atlitech/reports). To build and test:

```bash
dotnet tool restore
dotnet build
dotnet test                  # integration tests need Chrome or Chromium installed
dotnet test --project tests/Atli.Reports.AppHost.Tests   # end to end; also needs Docker and Bun
dotnet csharpier check .     # the formatting gate CI runs; `dotnet csharpier format .` fixes it
```

The [dev container](.devcontainer) comes with the .NET SDK, `chrome-headless-shell`, Node, and
Bun.

## License

Apache License 2.0. See [LICENSE](LICENSE).
