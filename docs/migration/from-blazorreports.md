# Migrating from BlazorReports to Atli.Reports.Blazor

BlazorReports is now Atli.Reports.Blazor. The last BlazorReports release is 0.25.1. Its
successor ships as `Atli.Reports.Blazor` 0.26.0, which is not on NuGet yet. Once 0.26.0 ships,
the `BlazorReports` and `BlazorReports.Components` packages will be deprecated in favor of
`Atli.Reports.Blazor`.

Most apps migrate in four steps:

1. Target .NET 10.
2. Swap the packages.
3. Rename the namespaces in `using`, `@using`, and `@inherits` directives.
4. Fix any compiler errors and warnings about obsolete APIs.

Type and method names are unchanged: `AddBlazorReports`, `MapBlazorReport`, `RegisterBlazorReport`,
`BlazorReportsBase`, `BlazorReportsTemplate`, `IReportService`, `BlazorReportsOptions`, and so
on. The sections below list every change, including the behavior changes an app may notice
without a compile error.

## Requirements

- **.NET 10 only.** `Atli.Reports.Blazor` and `Atli.Reports.Blazor.Components` target `net10.0`.
  BlazorReports 0.25.1 targeted `net8.0` and `net9.0`; apps that must stay on .NET 8 or 9 stay on
  BlazorReports 0.25.1.
- **A Chromium-based browser,** as before: Chrome, Chromium, or Edge installed on the machine, or
  any Chromium executable configured by path. `chrome-headless-shell` is now recommended where
  throughput matters; see [Browser requirements](../../README.md#browser-requirements).

## Packages

| BlazorReports 0.25.1 | Atli Reports 0.26.0 |
| --- | --- |
| `BlazorReports` | `Atli.Reports.Blazor` |
| `BlazorReports.Components` | `Atli.Reports.Blazor.Components` |
| (part of `BlazorReports`) | `Atli.Reports.Engine`, a new dependency of `Atli.Reports.Blazor` |

```bash
dotnet remove package BlazorReports
dotnet add package Atli.Reports.Blazor
```

A Razor class library that only holds report components and references
`BlazorReports.Components` switches to `Atli.Reports.Blazor.Components`.

## Namespaces

Every `BlazorReports` namespace moves under `Atli.Reports.Blazor`:

| Before | After |
| --- | --- |
| `BlazorReports.Extensions` | `Atli.Reports.Blazor.Extensions` |
| `BlazorReports.Models` | `Atli.Reports.Blazor.Models` |
| `BlazorReports.Enums` | `Atli.Reports.Blazor.Enums` |
| `BlazorReports.Services` | `Atli.Reports.Blazor.Services` |
| `BlazorReports.Services.BrowserServices` | `Atli.Reports.Blazor.Services.BrowserServices` |
| `BlazorReports.Services.BrowserServices.Problems` | `Atli.Reports.Blazor.Services.BrowserServices.Problems` |
| `BlazorReports.Components` | `Atli.Reports.Blazor.Components` |

This applies to C# `using` directives, Razor `@using` and `@inherits` directives, and fully
qualified names. For example:

```razor
@inherits Atli.Reports.Blazor.Components.BlazorReportsBase
```

A regular-expression replace of `\bBlazorReports\.` with `Atli.Reports.Blazor.` covers all of
them. It leaves type names such as `BlazorReportsBase` and `BlazorReportsOptions` alone, because
they are not followed by a dot. Review the diff afterwards.

## Registration and engine configuration

`AddBlazorReports` works as before, and now also registers
[`Atli.Reports.Engine`](../../src/Atli.Reports.Engine), which owns the browser. It calls
`AddReportsEngine()`, which is idempotent: calling `AddBlazorReports` and `AddReportsEngine` in
either order, or several times, registers the engine once and applies every configuration.
Configure the engine with `AddReportsEngine`, in code or from configuration:

```csharp
builder.Services.AddBlazorReports();
builder.Services.AddReportsEngine(
  builder.Configuration.GetSection(ReportsEngineOptions.SectionName)
);
```

`BlazorReportsOptions.BrowserOptions` still works. `AddBlazorReports` copies each value you set to
something other than its default onto `ReportsEngineOptions.Browser`. Values left at their
defaults are not copied, so they do not overwrite settings made through `AddReportsEngine`.

| `BlazorReportsBrowserOptions` | `ReportsEngineOptions.Browser` |
| --- | --- |
| `Browser` (`Browsers.Chrome`, `Browsers.Edge`) | `Kind` (`BrowserKind.Chrome`, `BrowserKind.Edge`) |
| `BrowserExecutableLocation` (`FileInfo`) | `ExecutablePath` (`string`, the full path) |
| `DisableHeadless` | `Headless`, inverted |
| `ResponseTimeout` | `CommandTimeout` |
| `NoSandbox` | `NoSandbox` |
| `DisableDevShmUsage` | `DisableDevShmUsage` |
| `MaxBrowserPoolSize`, `MaxBrowserPagePoolSize` | None. Obsolete and ignored: the engine runs one browser |

The pool sizes no longer limit anything. Instead, `ReportsEngineOptions.Concurrency` bounds how
many reports render at once (`MaxConcurrentConversions`, by default the processor count clamped
to 2 to 8). It also bounds how many wait for a turn (`MaxQueueLength`, default 100) and for how
long (`QueueTimeout`, default 30 seconds). A report that finds the queue full or waits too long
fails with `ConversionErrorKind.Busy`. The
[configuration reference](../engine/architecture.md#configuration-reference) lists every engine
option.

## Endpoint responses

`MapBlazorReport` endpoints stream the report into the response as before. Failures that happen
before any of the report was sent now answer with RFC 9457 problem details
(`application/problem+json`). Before, they answered with an empty body. The status codes change
too:

| Failure (`ConversionErrorKind`) | BlazorReports 0.25.1 | Atli.Reports.Blazor |
| --- | --- | --- |
| Engine busy (`Busy`) | 503, empty body | 503 |
| Browser missing, failed to start, or crashed (`BrowserUnavailable`) | 500, empty body | **503** |
| Browser did not answer in time (`Timeout`) | 500, empty body | **504** |
| The report never called `blazorReport.completed()` (`SignalTimeout`) | (did not exist) | 504 |
| The client disconnected (`Canceled`) | 499 while waiting for a browser page, 500 once rendering had started | 499 |
| Invalid request (`InvalidRequest`) | 500, empty body | **400** |
| Rendering or printing failed (`RenderFailed`) | 500, empty body | 500 |

A failure after part of the report was sent fails the request. The server then aborts the
response, so the client sees a broken response instead of a truncated PDF with a `200`. The
endpoints' OpenAPI metadata now lists the 400, 500, 503, and 504 problem responses.

## `IReportService`

`IReportService` gains three overloads that write to a `Stream` and return
`OneOf<Success, ConversionError>`. `ConversionError.Kind` says why a report failed:

```csharp
var result = await reportService.GenerateReport(context.Response.Body, report, data, token);
if (result.TryPickT1(out var error, out _))
{
  // error.Kind: Busy, BrowserUnavailable, Timeout, SignalTimeout, RenderFailed, Canceled, ...
}
```

The `PipeWriter` overloads still work but are `[Obsolete]`. They fold every failure into the old
result types: `Busy` becomes `ServerBusyProblem`, `Canceled` becomes `OperationCancelledProblem`,
and everything else becomes `BrowserProblem`. As before, they complete the `PipeWriter` after a
PDF. Move to the `Stream` overloads, for example with `pipeWriter.AsStream()`. Projects that treat
warnings as errors see CS0618 errors until they do.

Also obsolete, and kept only so existing code compiles:

- `ServerBusyProblem`, `OperationCancelledProblem`, `BrowserProblem`, and `ConnectionProblem`.
  Nothing returns `ConnectionProblem` any more.
- `IBrowserService`, which remains resolvable as an adapter over the engine. Inject
  `Atli.Reports.Engine.IHtmlToPdfConverter` instead to convert HTML yourself.
- `BlazorReportsBrowserOptions.MaxBrowserPoolSize` and `MaxBrowserPagePoolSize` (see above).

**Custom `IReportService` implementations** must implement the three new `Stream` overloads. The
interface has no default implementations for them. **`ReportService`'s constructor** now takes an
`IHtmlToPdfConverter` instead of an `IBrowserService`:
`ReportService(IServiceProvider, BlazorReportRegistry, IHtmlToPdfConverter)`.

## Removed APIs

These public types exposed the old DevTools implementation and are gone without replacement:

- `BrowserResultResponse<T>`, `CreateTargetResponse`, `IoReadResponse`,
  `PageGetFrameTreeResponse`, `PagePrintToPdfResponse`
- `BrowserFrame`, `BrowserFrameTree`
- `LogMessages` (the source-generated log methods of the old browser service)

With them go their namespaces, `BlazorReports.Services.BrowserServices.Responses`, `.Types`, and
`.Logs`; remove `using` directives for them.

## New: waiting for a report's JavaScript

Reports that finish rendering in JavaScript can have the PDF printed only once that work is done.
Turn it on globally with `BlazorReportsOptions.JavaScriptSettings`, or per report with
`BlazorReportRegistrationOptions.JavaScriptSettings`. Each report starts from the global
settings.

```csharp
app.MapBlazorReport<SalesChart, SalesData>(options =>
{
  options.JavaScriptSettings.WaitForCompletedSignal = true;
  options.JavaScriptSettings.CompletedSignalTimeout = TimeSpan.FromSeconds(10); // default 30 s
});
```

The report calls `blazorReport.completed()` from its script when it is ready. A report that does
not fails with `SignalTimeout` (504 from a mapped endpoint). Reports that do not opt in render
exactly as before. `BlazorReportsTemplate` gains a `CompletedSignalName` parameter for this. A
negative timeout other than `Timeout.InfiniteTimeSpan` throws when the report is registered.

## Other behavior changes

- **One browser, isolated reports.** BlazorReports kept a pool of browsers and reused their pages.
  The engine runs one long-lived browser and renders every report in a fresh browser context, so
  cookies, storage, and cache never carry over from one report to the next. It restarts a crashed
  browser on the next report. It also replaces the browser after 1,000 reports or one hour, both
  configurable.
- **Styles and assets are read once per registry.** Each `BaseStylesPath` file and `AssetsPath`
  folder is read and encoded once. Every report that uses the same path shares the result. As
  before, edits made after a report is registered are not picked up until the app restarts.
- **Assets get ASP.NET Core's content types.** BlazorReports knew eight file extensions and encoded
  every other asset as `application/octet-stream`. Assets now take their type from ASP.NET Core's
  MIME table, so `.svg` images (`image/svg+xml`), fonts, and other common types are encoded with
  their real content type. Unknown extensions still fall back to `application/octet-stream`.
- **HTML output never starts the browser**, so `ReportOutputFormat.Html` reports work on machines
  without one.
- **`AddBlazorReports` no longer registers the `regex` route constraint.** BlazorReports registered
  it because `WebApplication.CreateSlimBuilder` leaves it out; report endpoints never use it. On
  `CreateSlimBuilder`, an app whose routes use `regex` constraints (Swashbuckle's `swagger.json`
  route does) now answers every request with a 500 until it registers the constraint itself:

  ```csharp
  using Microsoft.AspNetCore.Routing.Constraints;

  builder.Services.Configure<RouteOptions>(o =>
    o.SetParameterPolicy<RegexInlineRouteConstraint>("regex")
  );
  ```

## Client and viewer

`BlazorReports.Client` and `BlazorReports.Viewer` were never published and have been removed.
To convert over HTTP, use [`Atli.Reports.Client`](../../src/Atli.Reports.Client/README.md). It
implements `IHtmlToPdfConverter` against the reports server's `POST /convert` endpoint
([docs/engine/server.md](../engine/server.md)), so switching from the in-process engine to a
server only changes the registration:

```csharp
builder.Services.AddBlazorReports();
builder.AddReportsClient("reports"); // ConnectionStrings:reports = "Endpoint=http://reports:8080"
```

Reports still render in the app. The server converts them to PDF, and the app never starts a
browser. No viewer replaces `BlazorReports.Viewer`.
