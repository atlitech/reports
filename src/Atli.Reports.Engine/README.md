# Atli.Reports.Engine

HTML-to-PDF conversion for .NET 10. The engine renders HTML in headless Chrome, Chromium, or Edge
over the Chrome DevTools Protocol and returns the PDF. It is NativeAOT compatible.

- One long-lived browser serves all conversions, and each conversion gets a browser context of
  its own. Cookies, storage, and cache never leak from one document to the next.
- Concurrency is bounded, and further conversions wait in a FIFO queue. When the queue is full,
  a conversion fails fast with `Busy`.
- The PDF streams to your `Stream` as the browser produces it.
- JavaScript completion signals let a page say when it is ready to print.
- The engine recovers from browser crashes, recycles the browser, and can close it when idle.
- It publishes OpenTelemetry-ready traces and metrics and provides health checks.

Part of [Atli Reports](https://github.com/atlitech/reports).

## Install

```bash
dotnet add package Atli.Reports.Engine
```

## Convert HTML to PDF

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

Failures come back as a `ConversionError` whose `Kind` is one of `InvalidRequest`, `Busy`,
`BrowserUnavailable`, `Timeout`, `SignalTimeout`, `RenderFailed`, or `Canceled`. A second
`ConvertAsync` overload returns the PDF as an in-memory `Stream`.

## Configure

In a host (ASP.NET Core or a worker service), bind the options from the `ReportsEngine`
configuration section and add the health checks:

```csharp
builder.Services.AddReportsEngine(
  builder.Configuration.GetSection(ReportsEngineOptions.SectionName)
);
builder
  .Services.AddHealthChecks()
  .AddReportsEngineBrowserCheck(tags: ["ready"])
  .AddReportsEngineConversionCheck(tags: ["ready"]);
```

The browser check is unhealthy, with the reason, while the most recent browser launch has failed;
the engine retries the launch in the background until one succeeds, so readiness recovers without
waiting for a conversion. Keep it out of liveness probes: a restart does not repair a browser that
cannot start.

```json
{
  "ReportsEngine": {
    "Browser": { "WarmUpOnStartup": true },
    "Concurrency": { "MaxConcurrentConversions": 4, "MaxQueueLength": 50 },
    "ConversionTimeout": "00:01:00"
  }
}
```

The most used keys are `Browser:ExecutablePath`, `Browser:NoSandbox`,
`Browser:DisableDevShmUsage`, `Concurrency:MaxConcurrentConversions` (default: processor count,
2 to 8), `Concurrency:MaxQueueLength` (100), and `ConversionTimeout` (infinite). The
[configuration reference](https://github.com/atlitech/reports/blob/main/docs/engine/architecture.md#configuration-reference)
lists them all.

## Wait for JavaScript

```csharp
var result = await converter.ConvertAsync(
  html,
  new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TimeSpan.FromSeconds(10) }
);
```

The page calls `window.pdfReady()` when its asynchronous work is done, and the engine prints at
that moment. Without a signal, the engine prints after the `load` event, once web fonts are ready.

## Telemetry

The engine traces every conversion (a span per stage: queue wait, page open and browser launch,
content, load or signal wait, print, and streaming) and publishes metrics. Both use the name
`Atli.Reports.Engine`, available as constants:

```csharp
builder.Services.AddOpenTelemetry()
  .WithTracing(tracing => tracing.AddSource(ReportsEngineTelemetry.ActivitySourceName))
  .WithMetrics(metrics => metrics.AddMeter(ReportsEngineTelemetry.MeterName));
```

Spans and metrics never contain the HTML. Without a listener, the engine creates no spans.

## Browser

The engine finds Chrome, Chromium, or Edge in the standard install locations, or uses
`Browser:ExecutablePath`. For throughput, use
[`chrome-headless-shell`](https://googlechromelabs.github.io/chrome-for-testing/). With the full
desktop browser, creating a browser context per conversion caps throughput at about 7
conversions a second. In Linux containers, set `Browser:NoSandbox` (trusted HTML only) and
`Browser:DisableDevShmUsage`.

## Learn more

- [Atli Reports README](https://github.com/atlitech/reports#readme): quick starts and benchmarks
- [Engine architecture](https://github.com/atlitech/reports/blob/main/docs/engine/architecture.md):
  lifecycle, isolation, concurrency, streaming, and telemetry
- [Atli.Reports.Server](https://github.com/atlitech/reports/blob/main/docs/engine/server.md): the
  engine as an HTTP service in a container
- [Atli.Reports.Client](https://www.nuget.org/packages/Atli.Reports.Client): the same
  `IHtmlToPdfConverter`, converting on that server
