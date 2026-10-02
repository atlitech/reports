# Atli.Reports.Client

A typed client for the [Atli Reports server](https://github.com/atlitech/reports/blob/main/docs/engine/server.md).
It implements `IHtmlToPdfConverter` from
[Atli.Reports.Engine](https://www.nuget.org/packages/Atli.Reports.Engine) over HTTP. An app
switches between converting in process and converting on a server by changing one registration.
The client is NativeAOT compatible.

- The PDF streams from the server to your `Stream` without being buffered.
- Failures come back as the same `ConversionError` kinds the engine returns.
- A `503` is retried after the server's `Retry-After` delay, and so is a request that never
  reached the server. Other errors are returned at once.
- A health check probes the server's `/health/ready`.
- With [Atli.Reports.Blazor](https://www.nuget.org/packages/Atli.Reports.Blazor), components
  render in the app, the server converts them, and the app never starts a browser.

Part of [Atli Reports](https://github.com/atlitech/reports).

## Install

```bash
dotnet add package Atli.Reports.Client
```

## Register

In a host (ASP.NET Core, a worker service, or an Aspire project), name the connection string:

```csharp
builder.AddReportsClient("reports");
```

```json
{
  "ConnectionStrings": {
    "reports": "Endpoint=http://reports:8080"
  }
}
```

The connection string is `Endpoint=<url>`, which is the form the Aspire hosting integration
writes, or a bare URL. Settings come from the `ReportsClient` configuration section, then the
connection string, then the optional callback. Each source overrides the one before it:

```csharp
builder.AddReportsClient("reports", settings => settings.MaxRetryAttempts = 1);
```

Without a host builder, pass the settings yourself:

```csharp
services.AddReportsClient(new ReportsClientSettings { Endpoint = new Uri("http://reports:8080") });
```

Both overloads return the `IHttpClientBuilder` of the client's `HttpClient`, so you can add
handlers, for example for authentication.

## Authenticate

Set `ReportsClient__ApiKey` through your secret store to the full `key-id.secret` credential.
The Aspire hosting integration supplies `Endpoint=<url>;ApiKey=<credential>` through a secret
connection expression when configured with `WithApiKeyAuthentication`.

For workload identity, acquire an access token in code:

```csharp
builder.AddReportsClient("reports", settings =>
{
  settings.AccessTokenProvider = cancellationToken => AcquireReportsTokenAsync(cancellationToken);
});
```

`AcquireReportsTokenAsync` is your application's `ValueTask<string>` token callback; use your
identity provider's SDK, cache tokens until near expiry, and request the Reports API audience.
It runs for each conversion attempt within the attempt timeout. Do not configure both methods.
Health probes send no credentials. `401` and `403` return `Unauthorized` and `Forbidden` without
retrying; fix the credential or authorization assignment before resubmitting.

Use TLS across untrusted networks. Endpoints cannot contain credentials, query parameters, or
fragments. The default HTTP transport does not follow redirects, preventing documents and API
keys being forwarded to another origin. If you replace the primary handler, keep automatic
redirects disabled. Credentials are redacted from the client's HTTP header logs.

Aspire's service defaults add `AddStandardResilienceHandler` to every `HttpClient`. That handler
has a 10-second attempt timeout and retries `500` and `504`, both wrong for conversions, so the
client removes it from its own `HttpClient` and keeps only its pipeline.

## Convert

Use `IHtmlToPdfConverter` exactly as with the engine:

```csharp
var converter = app.Services.GetRequiredService<IHtmlToPdfConverter>();

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

Every `PdfOptions` value reaches the server, including custom paper sizes, header and footer
templates, page ranges, `GenerateTaggedPdf`, and `WaitForSignal` with any `WaitTimeout`.

There is one difference from the engine. The overload that returns a `Stream` gives you the
server's response as it arrives, so the stream is forward-only (`CanSeek` is `false`). If the
server breaks the transfer off, reading the stream throws an `IOException`. The overload that
writes to a destination stream returns that failure as a `ConversionError` instead.

## Errors

| Server answer | `ConversionErrorKind` |
| --- | --- |
| `400 Bad Request` | `InvalidRequest` |
| `401 Unauthorized` | `Unauthorized` |
| `403 Forbidden` | `Forbidden` |
| `422 Unprocessable Content` | `SignalTimeout` |
| `422` with `kind: PolicyDenied` | `PolicyDenied` |
| `503` with `kind: Busy` | `Busy`, after the retries |
| `503` with `kind: BrowserUnavailable` | `BrowserUnavailable`, after the retries |
| `504 Gateway Timeout` | `Timeout` |
| `500 Internal Server Error` | `RenderFailed` |
| Server unreachable, connection lost, or transfer broken off | `BrowserUnavailable` |
| `AttemptTimeout` or `TotalTimeout` reached | `Timeout` |
| The caller's `CancellationToken` canceled | `Canceled` |

The server's problem details carry a `kind` member, and the client uses it whenever it is
present. Without one, for example when a proxy answers, the status decides:

- other `4xx` codes map to `InvalidRequest`,
- `408` maps to `Timeout`,
- `429` maps to `Busy`,
- `502` and `503` map to `BrowserUnavailable`,
- every other status maps to `RenderFailed`.

A `200` that is not `application/pdf` is a `RenderFailed`, because the endpoint is probably not a
reports server.

A broken connection maps to `BrowserUnavailable` because that kind already means "nothing could
render the document right now; a later attempt may succeed".

## Settings

| Setting | Default | |
| --- | --- | --- |
| `Endpoint` | (required) | The server's base address. A path prefix is kept. |
| `ApiKey` | unset | Full API credential, sent in `X-Reports-Api-Key` on conversion requests. |
| `AccessTokenProvider` | unset | Code-only asynchronous bearer token callback. |
| `AttemptTimeout` | 2 minutes | How long one attempt waits for the server to start answering. |
| `TotalTimeout` | 5 minutes | The same, across all attempts and retry delays. |
| `MaxRetryAttempts` | 3 | Retries of a `503` or of a transport failure. `0` never retries. |
| `DisableHealthChecks` | `false` | Skips the `reports_server` health check (tagged `ready`). |
| `HealthCheckTimeout` | 5 seconds | How long the health check waits for `/health/ready`. |

The server starts answering when the first PDF byte is ready. By default it lets a conversion wait
30 seconds for a turn (`Concurrency:QueueTimeout`), and it gives the whole conversion 60 seconds
(`ConversionTimeout`). The client's defaults are longer, so the server's own `503` or `504` arrives
before the client gives up. If you raise the server's limits, raise the client's timeouts too. The
timeouts stop applying once the PDF starts to stream. From then on, the server's
`ConversionTimeout` and your `CancellationToken` bound the transfer.

## Blazor reports on a server

```csharp
builder.Services.AddBlazorReports();
builder.AddReportsClient("reports");
```

`AddReportsClient` replaces the engine's converter whichever of the two you call first. It also
turns off the engine's `WarmUpOnStartup`. The engine's other services stay registered but idle.
Components render in the app, the server converts the HTML, and the app needs no browser.

## Learn more

- [Atli Reports README](https://github.com/atlitech/reports#readme): quick starts and benchmarks
- [Atli.Reports.Server](https://github.com/atlitech/reports/blob/main/docs/engine/server.md): the
  `/convert` contract and the container image
- [Engine architecture](https://github.com/atlitech/reports/blob/main/docs/engine/architecture.md):
  lifecycle, isolation, concurrency, streaming, and metrics
