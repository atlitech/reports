# Reactive Signal Approach for JavaScript Rendering Completion

> **Status:** implemented in `Atli.Reports.Engine` as `PdfOptions.WaitForSignal` and
> `PdfOptions.WaitTimeout`. This is the original design note; its code sketches use the original
> type names (`ChromiumPage`, `SignalAwaiter`). [As implemented](#as-implemented) records where the
> shipped code differs from the proposal.

## Problem Statement

When converting HTML to PDF, we need to wait for dynamic JavaScript work (API calls, chart rendering, etc.) to complete before generating the PDF. Without a signal, there's no mechanism to detect when JavaScript has finished rendering.

## Solution: Reactive Signal (Like Angular Signals)

Use Chrome DevTools Protocol's `Runtime.addBinding` to create a **reactive, event-driven signal** that JavaScript can call when rendering is complete. This avoids CPU-intensive polling.

## How It Works

1. **.NET creates a "binding"** - exposes a callable function to JavaScript (e.g., `window.pdfReady()`)
2. **JavaScript calls that function** when rendering is complete
3. **.NET receives the event immediately** (reactive, not polled)
4. **Timeout** if the signal never arrives

## As implemented

The shipped code follows the design below, with these differences (see also
[architecture.md](architecture.md#load-and-signal-correctness)):

- **Registration happens before the HTML loads.** `IsolatedPage.EnableSignalAsync`
  (`src/Atli.Reports.Engine/Chromium/Page/IsolatedPage.cs`) sends `Page.enable`, `Runtime.enable`,
  `Runtime.addBinding`, and `Page.addScriptToEvaluateOnNewDocument` and waits for all four before
  `HtmlToPdfConverter` sets the content, so a page that signals immediately cannot race the
  registration.
- **The binding has the engine's own name.** The CDP binding is `__atliReportsSignal`. A script
  registered with `Page.addScriptToEvaluateOnNewDocument` (`SignalShim`,
  `src/Atli.Reports.Engine/Conversion/SignalShim.cs`) defines `window.<signal>()` as a zero-argument
  wrapper that calls the binding, because a binding requires exactly one string argument. The HTML
  itself is not modified. An earlier version prepended a `<script>` to the HTML, which put every
  document with a doctype into quirks mode.
- **The signal reaches every document.** `runImmediately` defines the function in the document that
  is already loaded (which `Page.setDocumentContent` keeps), and the enabled Page and Runtime domains
  carry the script and the binding into documents the page navigates to.
- **Events are routed per session.** Every conversion's page is a flat-mode session on the browser's
  single DevTools connection; `Runtime.bindingCalled` reaches only that page's handler.
- **No clean-up is needed.** Each conversion has its own page in its own browser context, discarded
  afterwards, so the binding and the script cannot leak into another conversion.
- **Outcomes are typed.** A missing signal fails the conversion with
  `ConversionErrorKind.SignalTimeout`; canceling the caller's token fails it with
  `ConversionErrorKind.Canceled`; a page that crashes while the engine waits fails it at once with
  `ConversionErrorKind.RenderFailed`.

## Implementation Details

### 1. Add WaitForSignalAsync to ChromiumPage

File: `src/Atli.Reports.Engine/Chromium/Page/ChromiumPage.cs`

```csharp
/// <summary>
/// Waits for JavaScript to call a signal function (reactive approach)
/// </summary>
/// <param name="signalName">Name of the signal function (e.g., "pdfReady" creates window.pdfReady())</param>
/// <param name="timeout">Timeout duration</param>
/// <param name="ct">Cancellation token</param>
/// <returns>Success or TimeoutError</returns>
public async ValueTask<OneOf<Success, TimeoutError>> WaitForSignalAsync(
    string signalName = "pdfReady",
    TimeSpan? timeout = null,
    CancellationToken ct = default
)
{
    timeout ??= TimeSpan.FromSeconds(30);
    var tcs = new TaskCompletionSource<bool>();

    // Step 1: Create binding that JavaScript can call
    DevToolsMessage addBindingMsg = new("Runtime.addBinding");
    addBindingMsg.Parameters.Add("name", signalName);
    _connection.SendAsync(addBindingMsg);

    // Step 2: Listen for bindingCalled event
    EventHandler<(string method, JsonElement parameters)> eventHandler = (sender, e) =>
    {
        if (e.method == "Runtime.bindingCalled")
        {
            var name = e.parameters.GetProperty("name").GetString();
            if (name == signalName)
            {
                tcs.TrySetResult(true);
            }
        }
    };

    // Note: DevToolsConnection needs to expose an OnEvent for this to work
    _connection.OnEvent += eventHandler;

    try
    {
        // Step 3: Wait for signal with timeout
        using var timeoutCts = new CancellationTokenSource(timeout.Value);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        await tcs.Task.WaitAsync(linkedCts.Token);
        return new Success();
    }
    catch (OperationCanceledException)
    {
        if (ct.IsCancellationRequested)
            throw;

        return new TimeoutError($"Signal '{signalName}' not received within {timeout.Value.TotalSeconds}s");
    }
    finally
    {
        _connection.OnEvent -= eventHandler;
    }
}
```

### 2. Update PdfOptions

File: `src/Atli.Reports.Engine/PdfOptions.cs`

```csharp
public sealed class PdfOptions
{
    // ... existing options ...

    /// <summary>
    /// Wait for JavaScript to call window[WaitForSignal]() before generating PDF.
    /// Example: If set to "pdfReady", JavaScript should call window.pdfReady() when rendering is complete.
    /// </summary>
    public string? WaitForSignal { get; set; }

    /// <summary>
    /// Timeout if signal is not received (default: 30 seconds)
    /// </summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

### 3. Update HtmlToPdfConverter

File: `src/Atli.Reports.Engine/Conversion/HtmlToPdfConverter.cs`

```csharp
// Step 3: Set HTML content
var contentResult = await page.SetContentAsync(html, ct);
if (contentResult.IsT1)
{
    LogMessages.FailedToSetHtmlContent(_logger);
    return new ConversionError(ConversionErrorKind.RenderFailed, "Failed to set HTML content");
}

// Step 3.5: Wait for reactive signal (if specified)
if (!string.IsNullOrEmpty(options.WaitForSignal))
{
    var signalResult = await page.WaitForSignalAsync(
        options.WaitForSignal,
        options.WaitTimeout,
        ct
    );

    if (signalResult.TryPickT1(out var timeoutError, out _))
    {
        LogMessages.SignalTimeout(_logger, options.WaitForSignal, options.WaitTimeout);
        return new ConversionError(
            ConversionErrorKind.SignalTimeout,
            $"Timeout waiting for signal '{options.WaitForSignal}' after {options.WaitTimeout.TotalSeconds}s"
        );
    }
}

// Step 4: Generate PDF
var pdfStream = new MemoryStream();
// ... rest of PDF generation
```

### 4. Update DevToolsConnection (Required)

File: `src/Atli.Reports.Engine/Chromium/Connection/DevToolsConnection.cs`

The `DevToolsConnection` class needs to expose an event for Chrome DevTools events:

```csharp
internal sealed class DevToolsConnection
{
    // Add event handler for DevTools events
    public event EventHandler<(string method, JsonElement parameters)>? OnEvent;

    // In the WebSocket message handling code:
    private void HandleIncomingMessage(string json)
    {
        var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Check if this is an event (has "method" but no "id")
        if (root.TryGetProperty("method", out var methodElement) &&
            !root.TryGetProperty("id", out _))
        {
            var method = methodElement.GetString() ?? "";
            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement
                : default;

            OnEvent?.Invoke(this, (method, parameters));
        }
    }
}
```

## User API

### JavaScript Side

```html
<!DOCTYPE html>
<html>
<head>
    <title>Dynamic Report</title>
    <script src="https://cdn.jsdelivr.net/npm/chart.js"></script>
</head>
<body>
    <h1>Sales Report</h1>
    <canvas id="chart"></canvas>

    <script>
        // Do async work - fetch data and render charts
        async function renderReport() {
            try {
                // Fetch data from API
                const response = await fetch('/api/sales-data');
                const data = await response.json();

                // Render chart
                const ctx = document.getElementById('chart').getContext('2d');
                new Chart(ctx, {
                    type: 'bar',
                    data: data
                });

                // Wait for chart to fully render
                await new Promise(resolve => setTimeout(resolve, 100));

                // Signal to .NET that we're ready!
                window.pdfReady();

            } catch (error) {
                console.error('Failed to render report:', error);
                // Still signal so PDF generation doesn't hang forever
                window.pdfReady();
            }
        }

        renderReport();
    </script>
</body>
</html>
```

### C# Side

```csharp
// IHtmlToPdfConverter is registered by services.AddReportsEngine().
var html = File.ReadAllText("dynamic-report.html");

var options = new PdfOptions
{
    WaitForSignal = "pdfReady",  // Wait for window.pdfReady() to be called
    WaitTimeout = TimeSpan.FromSeconds(30),
    PrintBackground = true
};

await using var file = File.Create("report.pdf");
var result = await converter.ConvertAsync(html, file, options);

result.Switch(
    _ => Console.WriteLine("PDF generated successfully!"),
    error => Console.WriteLine(
        error.Kind == ConversionErrorKind.SignalTimeout
            ? "The page never called window.pdfReady()."
            : $"Error: {error.Message}"
    )
);
```

## Benefits vs. Polling Approach

| Aspect | Reactive Signal | Polling |
|--------|----------------|---------|
| **Response Time** | Instant (0ms delay) | 100-500ms delay |
| **CPU Usage** | Minimal (event-driven) | Continuous (checks every interval) |
| **Accuracy** | Exact moment of completion | Approximate (dependent on interval) |
| **Complexity** | Requires event handling in DevToolsConnection | Simpler implementation |
| **User Experience** | Better (like Angular signals) | Acceptable but less efficient |
| **Industry Standard** | Used by Puppeteer/Playwright | Legacy approach |

## Alternative Approaches Considered

### 1. Polling (Not Recommended)
```csharp
// Periodically check if window.reportReady === true
while (!ready && elapsed < timeout)
{
    await Task.Delay(100);
    ready = await page.EvaluateAsync("window.reportReady === true");
}
```
**Downside**: Wastes CPU, has inherent delay

### 2. Wait for Network Idle (Good for specific cases)
```csharp
// Wait until no network requests for 500ms
await page.WaitForNetworkIdleAsync();
```
**Downside**: Doesn't work for setTimeout or WebSocket-based rendering

### 3. Wait for Selector (Limited)
```csharp
// Wait for specific element to appear
await page.WaitForSelectorAsync("#rendering-complete");
```
**Downside**: Requires DOM manipulation, less flexible

## Recommendation

**Use the Reactive Signal approach** as the primary mechanism because:
- Most flexible - works with any async pattern
- Most efficient - event-driven, no polling
- Best user experience - instant response
- Industry standard - used by modern browser automation tools

Optionally support Network Idle as a convenience for cases where users don't want to modify JavaScript.

## Implementation Checklist

- [x] Route CDP events to the page (shipped as `DevToolsSession.EventReceived`)
- [x] Register the signal on the page (shipped as `IsolatedPage.EnableSignalAsync` and `WaitForSignalAsync`)
- [x] Add `WaitForSignal` and `WaitTimeout` properties to `PdfOptions`
- [x] Update `HtmlToPdfConverter` to wait for the signal when configured
- [x] Add logging for signal timeout scenarios
- [x] Write tests for the signal mechanism (`tests/Atli.Reports.Engine.Tests`)
- [ ] Update documentation with examples
- [ ] Add sample showing dynamic chart rendering with signal

## References

- [Chrome DevTools Protocol - Runtime.addBinding](https://chromedevtools.github.io/devtools-protocol/tot/Runtime/#method-addBinding)
- [Chrome DevTools Protocol - Runtime.bindingCalled](https://chromedevtools.github.io/devtools-protocol/tot/Runtime/#event-bindingCalled)
- Puppeteer's waitForFunction: Uses polling internally
- Playwright's waitForFunction: Uses bindings for better performance
