# JavaScript completion signals

HTML that renders charts, fetches data, or updates the DOM asynchronously can tell the engine
when it is ready to print. Set `PdfOptions.WaitForSignal` to a function name and call that
function from the page after its work finishes. Without a signal, the engine waits for the
page's `load` event and `document.fonts.ready`.

## Using the engine

With `IHtmlToPdfConverter` registered by `services.AddReportsEngine()`:

```csharp
var html = """
  <!DOCTYPE html>
  <p id="result">Loading...</p>
  <script>
    setTimeout(function () {
      document.getElementById('result').textContent = 'Ready to print';
      window.pdfReady();
    }, 500);
  </script>
  """;

await using var file = File.Create("report.pdf");
var result = await converter.ConvertAsync(html, file, new PdfOptions
{
  WaitForSignal = "pdfReady",
  WaitTimeout = TimeSpan.FromSeconds(10)
});

if (result.TryPickT1(out var error, out _))
{
  Console.Error.WriteLine($"{error.Kind}: {error.Message}");
}
```

`WaitTimeout` defaults to 30 seconds. A missing signal returns
`ConversionErrorKind.SignalTimeout`; caller cancellation returns `Canceled`. The overall
conversion timeout still applies, including when `WaitTimeout` is `Timeout.InfiniteTimeSpan`.
The page must finish all required work, including loading any assets it needs, before signaling.

## Using Blazor reports

Enable the signal globally through `AddBlazorReports(options => options.JavaScriptSettings...)`
or for a particular report:

```csharp
app.MapBlazorReport<SalesChart, SalesData>(options =>
{
  options.JavaScriptSettings.WaitForCompletedSignal = true;
  options.JavaScriptSettings.CompletedSignalTimeout = TimeSpan.FromSeconds(10);
});
```

The component calls `blazorReport.completed()` once its JavaScript finishes. The report template
forwards that call to the engine. The same template and settings apply when generating a
registered report through `IReportService`. In HTML output, the completion call does nothing
and no browser is started.

Reports can also set the engine's `PdfOptions.WaitForSignal` and `WaitTimeout` directly. The
template forwards `blazorReport.completed()` to the configured signal function. Enabling
`JavaScriptSettings.WaitForCompletedSignal` takes precedence over those two PDF options for that
conversion; it does not modify the report's configured options.

See [the asynchronous JavaScript example](../../examples/SimpleReportServer/AsyncJavaScriptReport.razor).
The [server API](server.md) exposes the engine options as `waitForSignal` and `waitTimeoutSeconds`.

## Why the signal uses a DevTools binding

The engine subscribes to `Runtime.bindingCalled` events instead of polling the page. Before
loading HTML, it enables the Page and Runtime domains, registers its `__atliReportsSignal`
binding, and installs a wrapper with `Page.addScriptToEvaluateOnNewDocument`. The wrapper exposes
the requested function and supplies the string argument that the binding requires.

Registration finishes before the page's own scripts run, so even an immediate signal is retained.
The wrapper is installed in the existing document and documents reached through navigation.
It does not prepend anything to the HTML, which preserves the document's doctype and layout mode.
Events are routed to the conversion's session; disposing its isolated browser context removes
the page, binding, and script together.

The implementation is in
[`IsolatedPage`](../../src/Atli.Reports.Engine/Chromium/Page/IsolatedPage.cs) and
[`SignalShim`](../../src/Atli.Reports.Engine/Conversion/SignalShim.cs). See also
[load and signal correctness](architecture.md#load-and-signal-correctness).
