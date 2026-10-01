# Atli Reports Server

`Atli.Reports.Server` is a small NativeAOT HTTP service over `Atli.Reports.Engine`. It ships as a
container image.

## `POST /convert`

The body is JSON: `{"html": "...", "options": {...}}`. The options mirror `PdfOptions`
(`orientation`, `paperSize`, `paperWidth`, `paperHeight`, `margins`, `printBackground`, `scale`,
`headerTemplate`, `footerTemplate`, `displayHeaderFooter`, `pageRanges`, `preferCSSPageSize`,
`generateTaggedPdf`, `waitForSignal`, `waitTimeoutSeconds`).

- `orientation` is `portrait` or `landscape` (case-insensitive); any other value is a `400`.
- `paperSize` is `letter`, `legal`, `a4`, or `a3` (case-insensitive). For any other size, set
  `paperWidth` and `paperHeight` in inches instead; together they override `paperSize`. An unknown
  `paperSize` name, only one of the two dimensions, or a dimension that is not greater than zero
  is a `400`.
- `generateTaggedPdf` asks for (`true`) or against (`false`) a tagged, accessible PDF. Omitted, the
  browser decides; current Chromium tags by default.
- `waitTimeoutSeconds` defaults to 30. With `waitForSignal` set, `-0.001`
  (`Timeout.InfiniteTimeSpan`) waits until the request is canceled, and other negative values are a
  `400`.

.NET apps can use [`Atli.Reports.Client`](../../src/Atli.Reports.Client/README.md) instead of
calling the endpoint directly. It sends this request and maps the answers back to
`ConversionError`s, implementing `IHtmlToPdfConverter` over the server.

**Success** is `200 OK` with `Content-Type: application/pdf` and
`Content-Disposition: attachment; filename=output.pdf`. The PDF streams into the response as the
browser produces it (chunked transfer encoding), without being buffered in the server first.

**Failures before the first PDF byte** are RFC 9457 problem details (`application/problem+json`)
with an extra `kind` member holding the `ConversionErrorKind`:

| Kind | Status | Notes |
| --- | --- | --- |
| `InvalidRequest` | 400 Bad Request | Blank HTML, unknown orientation or paper size, incomplete custom paper size, blank signal name, negative signal timeout |
| `SignalTimeout` | 422 Unprocessable Content | The document never called its signal; retrying the same document will not help |
| `Busy` | 503 Service Unavailable | `Retry-After: 1`; the queue is full or the wait for a turn timed out |
| `BrowserUnavailable` | 503 Service Unavailable | `Retry-After: 5`; the browser is restarting, missing, or the server is shutting down |
| `Timeout` | 504 Gateway Timeout | A browser command, the load wait, or `ConversionTimeout` ran out |
| `RenderFailed` | 500 Internal Server Error | The browser could not render or print (including rejected print options), or the page crashed |
| `Canceled` | 499 (no body) | The client disconnected; nothing reaches it, the status only shows in logs |

`SignalTimeout` deliberately differs from the 504 that `Atli.Reports.Blazor`'s `MapBlazorReport`
uses: there the server renders its own component, so a missing signal is a server-side timeout;
here the client sends the document, so it is a problem with the request.

**Failures after the first PDF byte** (the browser dies mid-transfer, say) abort the connection.
The client sees a broken response, never a truncated `200`.

## Health

- `GET /health/live`: the browser executable exists.
- `GET /health/ready`: the executable exists and recent conversions mostly succeed (busy, canceled,
  and invalid requests do not count).

## Configuration

Every engine option binds from the `ReportsEngine` section, so environment variables work as usual,
for example `ReportsEngine__Concurrency__MaxConcurrentConversions=4` or
`ReportsEngine__ConversionTimeout=00:00:45`. See [the engine's configuration
reference](architecture.md#configuration-reference). The server's `appsettings.json` sets:

- `Browser:WarmUpOnStartup: true`, so the first request does not pay the browser's start-up time;
- `ConversionTimeout: 00:01:00`, so overload ends in a clean `504` rather than a client timeout;
- `Browser:NoSandbox: true` and `Browser:DisableDevShmUsage: true`, for containers.

Request size is Kestrel's: 30 MB by default, configurable with `Kestrel__Limits__MaxRequestBodySize`.

## Telemetry

The server exports logs, metrics, and traces over OTLP once `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
Without it, OpenTelemetry is not registered at all. Everything follows the standard OpenTelemetry
environment variables, which .NET Aspire sets for the resources it runs (they also bind from
`appsettings.json` and the command line):

| Variable | Effect |
| --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Collector address, for example `http://otel-collector:4317`; switches the export on |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` (default) or `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Headers for the collector, for example an API key |
| `OTEL_SERVICE_NAME` | Service name; defaults to `atli-reports-server` |
| `OTEL_RESOURCE_ATTRIBUTES` | Extra resource attributes, for example `deployment.environment.name=staging` |

Once the export is on, the other standard settings, such as `OTEL_BSP_SCHEDULE_DELAY` or
`OTEL_METRIC_EXPORT_INTERVAL`, apply as in any OpenTelemetry .NET app.

- **Traces**: every request except the `/health` probes, with the engine's conversion spans
  (`atli.reports.convert` and its stages) below `POST /convert`. See
  [the engine's telemetry](architecture.md#telemetry) for span names and attributes.
- **Metrics**: ASP.NET Core and Kestrel (`http.server.request.duration` and friends), the .NET
  runtime (`System.Runtime`: GC, thread pool, exceptions), and the engine (`atli.reports.*`).
- **Logs**: whatever the `Logging` configuration lets through, with formatted messages and scopes,
  correlated with the trace that wrote them.

To look at it locally, start the [standalone Aspire
dashboard](https://learn.microsoft.com/dotnet/aspire/fundamentals/dashboard/standalone) (it takes
OTLP/gRPC on port 18889) or any OpenTelemetry Collector, and point the server at it:

```bash
docker run --rm -p 8080:8080 \
  -e OTEL_EXPORTER_OTLP_ENDPOINT=http://host.docker.internal:18889 \
  atli-reports-server
```

In this repository, `aspire start` runs the server under `examples/Atli.Reports.AppHost` with the
export already wired to the AppHost's dashboard.

## Container image

```bash
docker run --rm -p 8080:8080 ghcr.io/atlitech/reports-server:0.26.0
```

- **Build and runtime share Ubuntu 24.04** (`sdk:10.0-noble`, `runtime-deps:10.0-noble`), so the
  NativeAOT binary runs against the glibc it was linked with.
- **The browser is `chrome-headless-shell`** from Chrome for Testing (`linux64` or `linux-arm64`),
  which renders and isolates conversions several times faster than the full browser (see
  [architecture.md](architecture.md#isolation)). The `CHROME_VERSION` build argument pins a version;
  the default takes the current stable release, so a rebuild picks up browser security fixes. The
  image build fails if the browser links a shared library the image does not install (the `linux64`
  and `linux-arm64` builds link different sets).
- **`tini` is PID 1.** It forwards `SIGTERM` to the server, whose shutdown drains conversions and
  closes the browser cleanly, and it reaps the browser's exited child processes so none linger as
  zombies.
- **The server runs as the non-root `app` user.** The browser runs without its sandbox, which only
  suits trusted HTML.

### Published image

Each release publishes `ghcr.io/atlitech/reports-server` for `linux/amd64` and `linux/arm64`. Both
are built natively (not emulated) by the release workflow, which pins `CHROME_VERSION` to that day's
stable `chrome-headless-shell` and smoke-tests each architecture before any tag moves.

| Tag | Points at |
| --- | --- |
| `<version>`, for example `0.26.0` | That release. It never moves. |
| `<major>.<minor>`, for example `0.26` | The most recently published release of that line. A version with a pre-release suffix, such as `0.26.0-preview.1`, does not move it. |
| `latest` | The most recently published release that is not a pre-release, by version or by its GitHub release. |

Pin a version (or a digest) in production; `latest` and `<major>.<minor>` move with releases.

Each image carries OCI labels (`source`, `version`, `revision`, `licenses`), an SBOM, and a
BuildKit provenance attestation that records the build arguments, Chrome's version among them:

```bash
docker buildx imagetools inspect ghcr.io/atlitech/reports-server:0.26.0 --format '{{ json .SBOM }}'
docker buildx imagetools inspect ghcr.io/atlitech/reports-server:0.26.0 --format '{{ json .Provenance }}'
```

The multi-arch index also has a GitHub artifact attestation (SLSA build provenance, signed through
Sigstore) that ties it to the release workflow in this repository:

```bash
gh attestation verify oci://ghcr.io/atlitech/reports-server:0.26.0 --repo atlitech/reports
```

The attested subject is the multi-arch index, so verify a tag or the index digest
(`oci://ghcr.io/atlitech/reports-server@sha256:...`), not the per-platform digest that `docker pull`
resolves on one machine.

### Building the image

```bash
docker build -f src/Atli.Reports.Server/Dockerfile -t atli-reports-server .
docker run -p 8080:8080 atli-reports-server
```

`.github/scripts/smoke-test-server-image.sh atli-reports-server` runs the checks CI runs on every
image change: the server becomes ready, converts a document to a PDF, runs as a non-root user under
`tini`, and shuts down cleanly.

### Throughput

Measured with 50 one-page documents against a container limited to 2 CPUs and 2 GB (Linux arm64):

| | Sequential | 8 concurrent clients | Left in `/tmp` |
| --- | --- | --- | --- |
| Before (a browser per request, Debian Chromium) | 3.0 docs/s, p50 315 ms | 2.2 docs/s, p50 3.5 s | 187 entries after 101 requests |
| After (shared browser, isolated contexts, `chrome-headless-shell`) | 58.7 docs/s, p50 13 ms | 57.6 docs/s, p50 121 ms | the live profile only |
