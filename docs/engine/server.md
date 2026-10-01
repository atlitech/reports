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
- `waitTimeoutSeconds` defaults to 30 and is at most 4294967 (about 49 days, the longest wait .NET
  timers support). With `waitForSignal` set, `-0.001` (`Timeout.InfiniteTimeSpan`) waits until the
  request is canceled, and other negative values are a `400`. A value beyond ±4294967, or one that
  is not a finite number (`"NaN"`, `"Infinity"`), is a `400` either way.
- The body must be one JSON object sent as `application/json`. Malformed JSON, a missing `html`, a
  field of the wrong type, and a property given twice (`{"html": "a", "html": "b"}`, in any letter
  case) are a `400`; another content type is a `415`.

.NET apps can use [`Atli.Reports.Client`](../../src/Atli.Reports.Client/README.md) instead of
calling the endpoint directly. It sends this request and maps the answers back to
`ConversionError`s, implementing `IHtmlToPdfConverter` over the server.

**Success** is `200 OK` with `Content-Type: application/pdf` and
`Content-Disposition: attachment; filename=output.pdf`. The PDF streams into the response as the
browser produces it (chunked transfer encoding), without being buffered in the server first.

**Failures before the first PDF byte** are RFC 9457 problem details (`application/problem+json`)
with an extra `kind` member holding the `ConversionErrorKind`. That includes requests the endpoint
never sees, because their body could not be read, and unexpected server errors:

| Kind | Status | Notes |
| --- | --- | --- |
| `InvalidRequest` | 400 Bad Request | A body that is not a conversion request (see above), blank HTML, unknown orientation or paper size, incomplete custom paper size, blank signal name, negative or out-of-range signal timeout |
| `InvalidRequest` | 413 Content Too Large | The body is larger than `Kestrel:Limits:MaxRequestBodySize` (see [Configuration](#configuration)) |
| `InvalidRequest` | 415 Unsupported Media Type | The body is not sent as `application/json` |
| `SignalTimeout` | 422 Unprocessable Content | The document never called its signal; retrying the same document will not help |
| `Busy` | 503 Service Unavailable | `Retry-After: 1`; the queue is full or the wait for a turn timed out |
| `BrowserUnavailable` | 503 Service Unavailable | `Retry-After: 5`; the browser is restarting, missing, or the server is shutting down |
| `Timeout` | 504 Gateway Timeout | A browser command, the load wait, or `ConversionTimeout` ran out |
| `RenderFailed` | 500 Internal Server Error | The browser could not render or print (including rejected print options), or the page crashed; also an unexpected server error, whose details stay in the server's log |
| `Canceled` | 499 (no body) | The client disconnected; nothing reaches it, the status only shows in logs |

Any other error status, such as the `404` of an unknown path or the `405` of `GET /convert`, is
problem details too, with the kind `Atli.Reports.Client` infers from a bare status (for example
`InvalidRequest` for a `404`, `RenderFailed` for a `500`), so naming it changes nothing for the
client.

`SignalTimeout` deliberately differs from the 504 that `Atli.Reports.Blazor`'s `MapBlazorReport`
uses: there the server renders its own component, so a missing signal is a server-side timeout;
here the client sends the document, so it is a problem with the request.

**Failures after the first PDF byte** (the browser dies mid-transfer, say, or the server fails
unexpectedly) abort the connection. The client sees a broken response, never a truncated `200`.

## OpenAPI

`GET /openapi/v1.json` returns the server's OpenAPI 3.1 document, in every environment. It describes
`POST /convert` as above, for client generators and API tools:

- the request body, with a description of every option and the values of `orientation` and
  `paperSize` (the document lists them in lower case; the server accepts any case);
- the `200` response as binary `application/pdf` content;
- a problem details response for each status in the table above except the bodiless `499`, with
  the `ConversionErrorKind` names as the values of `kind`, and the `Retry-After` header of the
  `503`.

Its `info.version` is the server's release. The `/health` endpoints are left out: they serve
probes, not clients.

## Health

- `GET /health/live`: the server is up and answering. It runs no engine check: restarting the
  process does not repair a browser that cannot start, and a browser that is slow to start under
  load must not get a working server killed.
- `GET /health/ready`: the server can take conversions. It answers `200` when both of its checks
  are healthy and `503` otherwise:
  - `browser`: whether the engine can launch its browser. Unhealthy while the most recent launch
    has failed (the browser exited at once, for example because a shared library is missing, or did
    not report its DevTools endpoint within `Browser:StartupTimeout`), with the reason, until a
    launch succeeds. Healthy while the browser runs. Before the first launch (when
    `Browser:WarmUpOnStartup` is off) and after the browser closed (idle, recycled, crashed), healthy
    as long as the executable the next launch would start exists.
  - `conversion_health`: unhealthy when recent conversions mostly failed (busy, canceled, and
    invalid requests do not count).

A failed launch does not keep the server out of rotation for good. The engine retries the launch in
the background, after 1 second and then doubling up to 30 seconds, until one succeeds, so readiness
recovers on its own once the cause is fixed, without waiting for a conversion. A retry never runs
beside a conversion's own launch: only one launch runs at a time. Shutdown stops the retries at
once. Each attempt is an `atli.reports.browser.launch` span (with
`atli.reports.browser.launch.attempt`) and logs event 301 when it fails and 309 before the next
retry; event 310 marks the recovery.

The body reports every check:

```json
{
  "status": "Unhealthy",
  "checks": {
    "browser": {
      "status": "Unhealthy",
      "description": "The browser failed to start (3 failed launch(es) in a row); retrying in the background. The browser exited with code 127 before it reported its DevTools endpoint. Browser output: /opt/chrome-headless-shell/chrome-headless-shell: error while loading shared libraries: libnss3.so: cannot open shared object file: No such file or directory"
    },
    "conversion_health": { "status": "Healthy", "description": "No conversions yet." }
  }
}
```

The reason quotes the browser's output, and health endpoints are usually reachable without
authentication, so the check reduces it to one line of at most 500 characters and masks URL
credentials, DevTools target ids, and secret-looking parameters (`token=`, `--password=`, ...).

## Configuration

Every engine option binds from the `ReportsEngine` section, so environment variables work as usual,
for example `ReportsEngine__Concurrency__MaxConcurrentConversions=4` or
`ReportsEngine__ConversionTimeout=00:00:45`. See [the engine's configuration
reference](architecture.md#configuration-reference). The server's `appsettings.json` sets:

- `Browser:WarmUpOnStartup: true`, so the first request does not pay the browser's start-up time;
- `ConversionTimeout: 00:01:00`, so overload ends in a clean `504` rather than a client timeout;
- `Browser:NoSandbox: true` and `Browser:DisableDevShmUsage: true`, for containers.

Request limits are Kestrel's, and the `Kestrel:Limits` section binds onto them, so they are
configurable like any other setting:

- `Kestrel__Limits__MaxRequestBodySize`, in bytes, caps the request body: 30,000,000 (about 30 MB)
  by default. A larger body is a `413` with problem details.
- The other `KestrelServerLimits` settings bind the same way, for example
  `Kestrel__Limits__MaxRequestHeadersTotalSize`, `Kestrel__Limits__KeepAliveTimeout`, or
  `Kestrel__Limits__Http2__MaxStreamsPerConnection`.

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
- **Metrics**: ASP.NET Core and Kestrel (`http.server.request.duration` and friends, without the
  `/health` probes), the .NET runtime (`System.Runtime`: GC, thread pool, exceptions), and the
  engine (`atli.reports.*`).
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

- **Build and runtime share Ubuntu 24.04** (`sdk:10.0-noble-aot`, which carries the NativeAOT
  toolchain, and `runtime-deps:10.0-noble`), so the NativeAOT binary runs against the glibc it was
  linked with.
- **The browser is `chrome-headless-shell`** from Chrome for Testing (`linux64` or `linux-arm64`),
  which renders and isolates conversions several times faster than the full browser (see
  [architecture.md](architecture.md#isolation)). The `CHROME_VERSION` build argument pins a version;
  the default takes the current stable release, so a rebuild picks up browser security fixes. The
  image build fails if the browser links a shared library the image does not install (the `linux64`
  and `linux-arm64` builds link different sets).
- **`tini` is PID 1.** It forwards `SIGTERM` to the server, whose shutdown drains conversions and
  closes the browser cleanly, and it reaps the browser's exited child processes so none linger as
  zombies.
- **The server runs as the non-root `app` user** (UID 1654, set by number so Kubernetes'
  `runAsNonRoot` can verify it). The browser runs without its sandbox, which only suits trusted
  HTML.

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
image change: the server becomes ready, converts a document to a PDF, serves its OpenAPI document,
runs as a non-root user under `tini`, and shuts down cleanly.

### Throughput

Measured with 50 one-page documents against a container limited to 2 CPUs and 2 GB (Linux arm64):

| | Sequential | 8 concurrent clients | Left in `/tmp` |
| --- | --- | --- | --- |
| Before (a browser per request, Debian Chromium) | 3.0 docs/s, p50 315 ms | 2.2 docs/s, p50 3.5 s | 187 entries after 101 requests |
| After (shared browser, isolated contexts, `chrome-headless-shell`) | 58.7 docs/s, p50 13 ms | 57.6 docs/s, p50 121 ms | the live profile only |
