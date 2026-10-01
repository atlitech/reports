# Engine architecture: one browser, isolated conversions

`Atli.Reports.Engine` renders HTML in a Chromium-based browser over the Chrome DevTools Protocol
(CDP). This note describes how the engine runs that browser in production: its lifecycle, how
conversions are isolated from each other, how load is bounded, and how the PDF leaves the browser.
It records the measurements behind the choices.

Code map (all under `src/Atli.Reports.Engine/`):

| Concern | Type |
| --- | --- |
| Browser process: launch, endpoint discovery, kill, profile cleanup | `Chromium/Browser/BrowserProcess.cs` |
| One running browser and its connection; per-conversion pages | `Chromium/Browser/BrowserInstance.cs` |
| Lazy start, crash recovery, recycling, idle close, shutdown | `Chromium/Browser/BrowserManager.cs` |
| An isolated page: signal, content, load wait, print | `Chromium/Page/IsolatedPage.cs` |
| CDP transport | `Chromium/Connection/DevToolsConnection.cs`, `DevToolsSession.cs` |
| Bounded concurrency and FIFO queue | `Conversion/ConversionLimiter.cs` |
| The conversion flow and error mapping | `Conversion/HtmlToPdfConverter.cs`, `ConversionErrors.cs` |
| PDF streaming | `Pdf/ChromiumPdfGenerator.cs` |
| Host integration (warm-up, drain on stop) | `Hosting/ReportsEngineHostedService.cs` |
| Telemetry names (public) | `ReportsEngineTelemetry.cs` |
| Traces | `Diagnostics/EngineActivities.cs` |
| Metrics | `Diagnostics/EngineMetrics.cs` |

## The conversion flow

1. **Validate** the request (`InvalidRequest` for blank HTML, a blank signal name, or a negative
   signal timeout). Nothing is queued or launched for an invalid request.
2. **Take a slot** from the limiter, or wait for one in a bounded FIFO queue (`Busy` when the queue
   is full or the wait exceeds `QueueTimeout`; `Canceled` if the caller cancels while waiting).
3. **Lease the browser.** The first conversion (or host start-up, with `WarmUpOnStartup`) launches
   it; later conversions reuse it.
4. **Open an isolated page**: `Target.createBrowserContext`, `Target.createTarget` in that context,
   `Target.attachToTarget` in flat mode. The browser prepares the next page in the background, so a
   conversion usually finds one ready.
5. **Signal** (only with `PdfOptions.WaitForSignal`): `Page.enable`, `Runtime.enable`,
   `Runtime.addBinding`, and `Page.addScriptToEvaluateOnNewDocument` (with `runImmediately`), sent
   together.
6. **Set the content** with `Page.setDocumentContent` on the page's main frame.
7. **Wait**: for the signal, or, without one, for the `load` event and `document.fonts.ready`.
8. **Print** with `Page.printToPDF` (`transferMode: ReturnAsStream`), then **stream** the PDF to the
   destination with `IO.read`.
9. **Dispose the browser context**, which discards the page and everything it stored, and release
   the lease and the slot.

`ConversionTimeout` (off by default) bounds steps 2 to 8 as a whole; running out of it is `Timeout`.

## Browser lifecycle

**One long-lived process.** `BrowserManager` owns the engine's browser. It launches lazily on the
first conversion, or at host start-up with `Browser:WarmUpOnStartup`. Only one launch runs at a
time: every conversion that needs the browser waits for the same launch. A failed launch is
reported to the conversions waiting for it (`BrowserUnavailable`) and retried by the next one; it
never stops the host.

**Launching.** The browser starts with `--remote-debugging-port=0` and a fresh profile directory
(`$TMPDIR/atli-reports-<guid>`). The engine reads the DevTools endpoint from the browser's standard
error (`DevTools listening on ws://…`) instead of polling for the `DevToolsActivePort` file, and keeps
draining standard output and error (logged at debug level) so a chatty browser never blocks on a full
pipe. Arguments go through `ProcessStartInfo.ArgumentList`, so paths with spaces need no quoting.
If the browser exits before it reports its endpoint, the error includes its last lines of output
(for example "Running as root without --no-sandbox is not supported").

**Crash recovery.** The engine watches the process (`Process.Exited`, with `EnableRaisingEvents`) and
the DevTools connection. When either ends unexpectedly, the browser is retired: conversions running
on it fail promptly with `BrowserUnavailable` (their pending commands fail as the connection closes),
what remains of the process tree is killed, and the next conversion launches a replacement. If a
browser dies between the lease and the page, before any conversion work started, the conversion
retries once on a fresh browser, so "kill the browser, convert again" succeeds.

**Page crashes.** A renderer that crashes (an out-of-memory kill, say) raises
`Inspector.targetCrashed` on the page's session, and Chromium never answers the commands pending on
it. The engine fails the session and its pending commands at once, so the conversion ends with
`RenderFailed` instead of sitting out its signal or command timeout. The browser itself stays up.

**Recycling.** A browser is replaced after `Browser:MaxConversionsPerProcess` conversions (default
1000; `0` disables) or after `Browser:MaxProcessLifetime` (default one hour; checked when a conversion
starts). Recycling drains: the old browser takes no new conversions, the replacement starts launching
at once, conversions already running finish on the old browser, and it closes when the last one is
done. Over 8,000 conversions with recycling disabled, the container's memory stayed flat (about 135
MiB), so the defaults are a safety margin rather than a necessity.

**Idle close.** With `Browser:IdleTimeout` set (off by default), a browser without conversions for
that long is closed; the next conversion starts a new one. This lets a host drop back to the server's
own footprint between bursts of work.

**Shutdown.** When the host stops (the hosted service's `StopAsync`) or the service provider is
disposed, the engine stops accepting conversions, waits up to `Browser:ShutdownTimeout` (default 10 s,
also bounded by the host's shutdown timeout) for running ones, and then closes every browser. Closing
always runs in the same order: kill the whole process tree (`Process.Kill(entireProcessTree: true)`),
wait for the browser to exit, and only then delete the profile directory, retrying while the file
system still holds it. A launch that fails after the process started cleans up the same way. Before
this change, about half of all profile directories leaked, and a browser that failed to start kept
running.

## Isolation

Atli renders documents for different tenants in the same browser, so nothing one conversion stores
may be visible to the next: cookies, `localStorage` and `sessionStorage`, IndexedDB, Cache Storage,
the HTTP cache, service workers, permissions, bindings, and injected scripts.

**Choice: a browser context per conversion.** A browser context is Chromium's own isolation boundary
(it is what an incognito window is): it has its own cookie jar, storage partition, HTTP cache, and
service workers, and `Target.disposeBrowserContext` discards all of it. Every conversion gets a new
context and a new page in it; bindings and scripts registered for the page die with it.

**Rejected: reusing pages with a reset.** Reusing a warm page and clearing state between conversions
is faster, but the reset has to enumerate everything a page can store, across every origin it
touched (CDP's `Storage.clearDataForOrigin` takes one origin at a time), and has to keep up with new
storage APIs. It cannot be shown complete, and a gap leaks data between tenants. A new target in the
shared default context is not isolated either: the integration test
`IsolationTests.Cookies_and_storage_written_in_one_conversion_are_invisible_to_the_next` fails in that
setup (cookies and `localStorage` from the first conversion are visible to the second), as does
`Cached_responses_are_not_shared_between_conversions`.

**Measurements.** Per-conversion latency (p50) and throughput for a one-page invoice, Chromium 154,
Linux arm64 container on a 10-core machine, measured with a raw CDP client (median of 60 to 120
conversions per cell):

| Strategy | Full Chromium (`--headless`), C=1 | C=4 | C=8 | `chrome-headless-shell`, C=1 | C=4 | C=8 |
| --- | --- | --- | --- | --- | --- | --- |
| New context per conversion | 75 ms, 12.8/s | 6.9/s | 7.3/s | 21 ms, 43/s | 116/s | 99/s |
| New context, prepared in advance | 97 ms, 9.5/s | 7.7/s | 6.8/s | 11 ms, 84/s | 121/s | 95/s |
| New target, shared context (not isolated) | | | 39/s | | | 133/s |
| Reused page with reset (not provably isolated) | 28 ms, 30/s | 42/s | 41/s | 8 ms, 122/s | 258/s | 305/s |

Two findings shaped the implementation:

- **The browser build matters more than the strategy.** In the full browser, creating and destroying
  a browser context is expensive work on the browser's main thread (each context is an off-the-record
  profile with its own services), so throughput stops at about 7 conversions a second no matter how
  many cores there are. `chrome-headless-shell`, Chromium's dedicated headless build, does the same
  in a few milliseconds (create context 5 ms, create target 9 ms, attach 9 ms at C=8) and prints
  about four times faster. The server image therefore ships `chrome-headless-shell`, and it is the
  recommended executable wherever throughput matters (set `Browser:ExecutablePath`).
- **Preparing the next page in advance** halves single-conversion latency with
  `chrome-headless-shell` (21 ms to 11 ms) and costs nothing at higher concurrency, so the engine keeps
  one isolated page ready per browser. The prepared page is ours (a blank document), so it carries no
  tenant state.

Isolation per context costs between a third and two thirds of the throughput of the unsafe page
reuse on `chrome-headless-shell`; it is the price of guarantees that the reset cannot give.

## Concurrency and backpressure

`ConversionLimiter` admits at most `Concurrency:MaxConcurrentConversions` conversions at once
(default: the processor count, at least 2 and at most 8). The rest wait in a strictly first-in,
first-out queue of at most `Concurrency:MaxQueueLength` entries (default 100), each for at most
`Concurrency:QueueTimeout` (default 30 s). A released slot passes straight to the oldest waiter, so
arrivals cannot overtake the queue. A full queue or an expired wait is `Busy`; cancellation while
waiting is `Canceled` and leaves the queue.

The browser's main thread takes part in every conversion, so throughput grows little beyond a
handful of concurrent conversions. With `chrome-headless-shell` on 4 CPUs and 16 clients, limits of
2, 4, and 8 gave 102, 111, and 117 conversions a second.

All options are validated when the engine is first resolved (or at host start-up), and invalid values
fail with an `OptionsValidationException` listing every problem.

## Load and signal correctness

**Waiting for the page.** Without a signal, the engine evaluates a promise that resolves after the
`load` event and `document.fonts.ready`, so images, stylesheets, and web fonts are in before printing.
The wait is bounded by `Browser:CommandTimeout`. If the document navigates while the engine waits,
it waits for the new document instead.

**The signal.** `PdfOptions.WaitForSignal` used to be implemented by prepending a `<script>` to the
HTML. Anything before `<!DOCTYPE html>` switches a document to quirks mode, so every document that
waited for a signal laid out in quirks mode. The engine now exposes a CDP binding under its own name
(`__atliReportsSignal`) and registers a script with `Page.addScriptToEvaluateOnNewDocument` that
defines `window.<signal>()` as a zero-argument wrapper around it. The HTML is not modified, the
function exists before the page's own scripts run (`runImmediately` covers the document already
loaded, which `Page.setDocumentContent` keeps), and it also exists in documents the page navigates
to (which needs the Page and Runtime domains enabled). The wrapper looks the binding up when it is
called, so the order in which the browser installs the two does not matter.

## The DevTools connection

`DevToolsConnection` replaces the original transport, fixing these defects:

- **Lost replies (false timeouts).** A command used to be sent before its reply was registered; a
  fast reply was dropped and the command timed out. Commands are now registered first.
- **Large messages.** The receive loop read one 100 KB frame per message; a bigger message (a large
  PDF chunk, a big `Runtime.evaluate` result) broke the loop and with it the connection. Messages of
  any size (up to a 512 MB safety cap) are now reassembled from their frames.
- **Leaks.** Every reply allocated a `JsonDocument` that was never disposed, and the receive buffer
  leaked from the pool on some paths. Replies now hand over only their `result` object, in a pooled
  buffer the caller disposes; replies that arrive after their command timed out are released.
- **Timers.** Per-command timeouts used `Task.Delay` without cancelling it. They now use
  `Task.WaitAsync(timeout, cancellationToken)`, which cleans up its timer.
- **Sends.** Messages are written straight to UTF-8 JSON with relaxed escaping (HTML is not inflated
  sixfold by `<` escapes) and sent under a lock, so the socket never has two sends in flight. A
  send is never cancelled halfway, which would abort the WebSocket.
- **One connection.** Pages are attached as flat-mode sessions on the browser connection rather than
  each opening its own WebSocket. Events are routed to their session; a detached or crashed target
  fails its session's pending commands.

## Streaming

`Page.printToPDF` runs with `transferMode: ReturnAsStream`. The engine writes nothing to the
destination until the browser has printed successfully, then reads the stream with `IO.read` in
1 MiB chunks and writes each chunk as it arrives. The next chunk is requested before the current
one is written, so the browser reads while the destination writes. Chunks are base64-decoded
straight from the reply's UTF-8 JSON into a pooled buffer, with no intermediate strings. (The old
code decoded four bytes at a time and flushed after every three output bytes, and dropped data that
arrived with the end-of-file flag.) The buffered `ConvertAsync` overload streams into a
`MemoryStream`; the streaming overload writes to the caller's stream directly. Exceptions the
destination throws reach the caller unchanged.

## Telemetry

The engine publishes traces on the `Atli.Reports.Engine` activity source and metrics on the
`Atli.Reports.Engine` meter. `ReportsEngineTelemetry.ActivitySourceName` and
`ReportsEngineTelemetry.MeterName` hold the names:

```csharp
builder.Services.AddOpenTelemetry()
  .WithTracing(tracing => tracing.AddSource(ReportsEngineTelemetry.ActivitySourceName))
  .WithMetrics(metrics => metrics.AddMeter(ReportsEngineTelemetry.MeterName));
```

No span, attribute, event, metric, or log message contains the HTML being converted. Without a
listener for the source, `ActivitySource.StartActivity` returns `null` and the engine creates no
spans; attributes are computed only for sampled spans.

### Spans

Every call to either `ConvertAsync` overload is one `atli.reports.convert` span, a child of the
caller's current span (an ASP.NET Core request, say). Each stage the conversion reaches is a child
of it, in this order:

| Span | Parent | Covers |
| --- | --- | --- |
| `atli.reports.convert` | the caller's span | The whole conversion, queue wait included |
| `atli.reports.queue.wait` | conversion | Waiting for a slot; only conversions that had to wait have one |
| `atli.reports.page.open` | conversion | Leasing the browser and opening the isolated page (usually prepared in advance) |
| `atli.reports.browser.launch` | `page.open`, or none | Starting a browser and connecting to it (see below) |
| `atli.reports.page.set_content` | conversion | Registering the completion signal, if any, and `Page.setDocumentContent` |
| `atli.reports.page.wait` | conversion | Waiting for the `load` event and fonts, or for the signal |
| `atli.reports.pdf.print` | conversion | `Page.printToPDF` and the streaming below |
| `atli.reports.pdf.stream` | `pdf.print` | Moving the printed PDF out of the browser into the destination with `IO.read` |

A browser launch is a child of the `page.open` span of the conversion that needed it; conversions
that arrive during the launch wait for the same launch inside their own `page.open`. The launch at
host start-up (`Browser:WarmUpOnStartup`) and the background launch that replaces a browser retired
for its conversion count have no parent: they are traces of their own. Work that outlives a
conversion (the crash watcher, the idle timer, the background launch) does not run in that
conversion's execution context, so its logs are not attributed to it.

Attributes:

| Attribute | Span | Value |
| --- | --- | --- |
| `error.type` | `convert` | The `ConversionErrorKind` (`Timeout`, `Busy`, ...), when the conversion fails |
| `atli.reports.html.length` | `convert` | Length of the HTML in characters (never its content) |
| `atli.reports.pdf.paper_size` | `convert` | `letter`, `legal`, `a4`, `a3`, or the size in inches (`8x10in`) |
| `atli.reports.pdf.orientation` | `convert` | `portrait` or `landscape` |
| `atli.reports.pdf.wait_for_signal` | `convert` | Whether the conversion waits for a completion signal |
| `atli.reports.pdf.tagged` | `convert` | `PdfOptions.GenerateTaggedPdf`, when set |
| `atli.reports.pdf.size` | `convert`, `pdf.print`, `pdf.stream` | PDF bytes written to the destination |
| `atli.reports.page.wait_for` | `page.wait` | `load` or `signal` |
| `atli.reports.browser.generation` | `page.open`, `browser.launch` | Which browser (1 for the first one launched) served the conversion or was launched |
| `atli.reports.browser.pid` | `browser.launch` | The browser's process id |
| `error.type` | `browser.launch` | The exception type, when the launch failed |

**Failures.** A failed conversion's span has the error status, with the `ConversionError` message
as its description, and `error.type` set to the `ConversionErrorKind`. The stage span the
conversion failed in also has the error status (a conversion that runs out of
`ConversionTimeout` shows there as canceled); an unexpected failure (a defect, logged as event 901)
adds an `exception` event to it. A failed launch has the error status, `error.type`, and an
`exception` event. When the caller's destination stream throws, the exception reaches the caller
unchanged, and the conversion span records its type as `error.type`.

**Events.** `atli.reports.browser.recycle` on the `page.open` span of the conversion that retired a
browser, with `atli.reports.browser.generation` and `atli.reports.browser.recycle.reason`
(`max_lifetime` or `max_conversions`).

**Browser lifecycle logs.** The rest of the browser's lifecycle is in structured log events
(category `Atli.Reports.Engine.*`), which an OpenTelemetry logging provider exports with the
current trace context: 300 browser started, 301 launch failed, 302 recycling (with the reason), 303
crashed or disconnected, 304 closed, 305 close failed, 306 shutdown drain timed out, 307 warm-up
failed, 308 idle close; 900 and 901 for failed conversions.

### Metrics

Published through `IMeterFactory` when the host registers one:

| Instrument | Type | Meaning |
| --- | --- | --- |
| `atli.reports.conversion.duration` | histogram (s) | Finished conversions, including the queue wait; tag `outcome` = `success` or the error kind |
| `atli.reports.conversion.active` | up-down counter | Conversions holding a slot |
| `atli.reports.queue.length` | up-down counter | Conversions waiting for a slot |
| `atli.reports.queue.wait` | histogram (s) | Time spent waiting for a slot |
| `atli.reports.browser.launches` | counter | Browser processes started |
| `atli.reports.browser.crashes` | counter | Browsers that exited or disconnected unexpectedly |
| `atli.reports.browser.recycles` | counter | Browsers replaced for age or conversion count |

### Blazor reports

`Atli.Reports.Blazor` traces on its own source, `Atli.Reports.Blazor`
(`BlazorReportsTelemetry.ActivitySourceName`): an `atli.reports.blazor.generate` span per report
(attributes `atli.reports.blazor.report`, `atli.reports.blazor.component`,
`atli.reports.blazor.output_format`, and `error.type` on failure), with an
`atli.reports.blazor.render` child for rendering the component and, for PDF output, the engine's
`atli.reports.convert` span.

## Configuration reference

All keys live under the `ReportsEngine` section (environment variables use `__`, for example
`ReportsEngine__Concurrency__MaxConcurrentConversions`).

| Key | Default | Meaning |
| --- | --- | --- |
| `Browser:Kind` | `Chrome` | Browser to look for when no path is set |
| `Browser:ExecutablePath` | found automatically | Browser executable; `chrome-headless-shell` recommended |
| `Browser:Headless` | `true` | Run without a window |
| `Browser:NoSandbox` | `false` | `--no-sandbox`; only for trusted HTML |
| `Browser:DisableDevShmUsage` | `false` | `--disable-dev-shm-usage`, for small `/dev/shm` in containers |
| `Browser:ExtraArguments:N` | none | Extra command-line switches, after the engine's own |
| `Browser:StartupTimeout` | `00:00:30` | Time for the browser to report its endpoint |
| `Browser:CommandTimeout` | `00:00:30` | Time per DevTools command; also bounds the load wait and printing |
| `Browser:WarmUpOnStartup` | `false` | Launch the browser with the host |
| `Browser:MaxConversionsPerProcess` | `1000` | Recycle after this many conversions; `0` never |
| `Browser:MaxProcessLifetime` | `01:00:00` | Recycle after this long; infinite (`-00:00:00.001`) never |
| `Browser:IdleTimeout` | infinite | Close the browser after this long without conversions |
| `Browser:ShutdownTimeout` | `00:00:10` | Time to drain running conversions on shutdown |
| `Concurrency:MaxConcurrentConversions` | processors, 2 to 8 | Conversions rendering at once |
| `Concurrency:MaxQueueLength` | `100` | Conversions waiting for a turn; `0` rejects at once |
| `Concurrency:QueueTimeout` | `00:00:30` | Longest wait for a turn; infinite waits until canceled |
| `ConversionTimeout` | infinite | Longest a whole conversion may take, queue wait included |

Per-conversion settings are on `PdfOptions`; `GenerateTaggedPdf` (default: the browser's choice,
which tags) maps to `generateTaggedPDF`. Tagged PDFs are larger and slower to produce for long,
table-heavy documents; set it to `false` when accessibility is not needed.
