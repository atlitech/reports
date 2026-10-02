# Atli Reports Server

`Atli.Reports.Server` is a small NativeAOT HTTP service over `Atli.Reports.Engine`. It ships as a
container image. Configure authentication before starting it; see [Security and production deployment](../security.md). The default document network policy rejects external assets.

The optional [isolated worker experiment](../isolated-workers.md) keeps this HTTP contract while
moving conversion into a separately launched worker. The integrated engine remains the default;
the deployment and browser configuration below describe that mode unless stated otherwise.

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
| `Unauthorized` | 401 Unauthorized | Missing, invalid, expired, or revoked credential |
| `Forbidden` | 403 Forbidden | Authenticated caller lacks permission |
| `PolicyDenied` | 422 Unprocessable Content | Document networking violates the host policy or an approved asset could not be fetched within its limits |
| `Busy` | 429 Too Many Requests | Caller admission limit; `Retry-After: 1` |
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

`GET /openapi/v1.json` returns the server's OpenAPI 3.1 document to callers with `reports.diagnostics`, in every environment. It describes
`POST /convert` as above, for client generators and API tools:

- the request body, with a description of every option and the values of `orientation` and
  `paperSize` (the document lists them in lower case; the server accepts any case);
- the `200` response as binary `application/pdf` content;
- a problem details response for each status in the table above except the bodiless `499`, with
  the `ConversionErrorKind` names as the values of `kind`, and the `Retry-After` header of the
  `429` and `503` responses.

Its `info.version` is the server's release. The `/health` endpoints are left out: they serve
probes, not clients.

## Health

In experimental worker mode, readiness uses `worker_execution` instead of the two engine checks.
It checks the launcher and configured Docker runtime (cached for five seconds), and refuses work
after uncertain cleanup. It does not pre-render a report or prove the configured image is usable.
The development process backend checks only that its executable exists. Liveness remains status-only.

- `GET /health/live`: the server is up and answering. It runs no engine check: restarting the
  process does not repair a browser that cannot start, and a browser that is slow to start under
  load must not get a working server killed.
- `GET /health/ready`: the server can take conversions. It answers `200` when both of its checks
  are healthy and `503` otherwise:
  - `browser`: whether the engine can launch its browser. Unhealthy while the most recent launch
    has failed (the browser exited at once, for example because a shared library is missing or
    Chromium could not create its sandbox, or did not report its DevTools endpoint within
    `Browser:StartupTimeout`), with the reason, until a launch succeeds. Healthy while the browser
    runs. Before the first launch (when
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

The anonymous probes return only the overall status. `GET /health/details`, protected by `reports.diagnostics`, reports every readiness check:

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

The protected detailed response can quote browser output. The check reduces it to one line of at
most 500 characters and masks URL credentials, DevTools target ids, and secret-looking parameters
(`token=`, `--password=`, ...). Anonymous probes never include this description.

When the container does not allow Chromium's sandbox (it runs without
[`deploy/seccomp/chromium.json`](../../deploy/seccomp/README.md), for example), the description
starts with what to do before the browser's own output, and the launch failure logged as event 301
starts the same way:

```text
The browser failed to start (3 failed launch(es) in a row); retrying in the background. Chromium could not create its sandbox, which needs unprivileged user namespaces. Run the container with the seccomp profile deploy/seccomp/chromium.json, and on Ubuntu 23.10+ make sure AppArmor allows user namespaces; or, for trusted HTML only, set ReportsEngine:Browser:NoSandbox=true. See docs/security.md#chromiums-sandbox. The browser exited with code 133 before it reported its DevTools endpoint. Browser output: [...:FATAL:content/browser/zygote_host/zygote_host_impl_linux.cc:129] No usable sandbox! ...
```

The server never falls back to running the browser without its sandbox. See
[Chromium's sandbox](../security.md#chromiums-sandbox).

## Configuration

Every engine option binds from the `ReportsEngine` section, so environment variables work as usual,
for example `ReportsEngine__Concurrency__MaxConcurrentConversions=4` or
`ReportsEngine__ConversionTimeout=00:00:45`. See [the engine's configuration
reference](architecture.md#configuration-reference). The server's `appsettings.json` sets:

- `Browser:WarmUpOnStartup: true`, so the first request does not pay the browser's start-up time;
- `ConversionTimeout: 00:01:00`, so overload ends in a clean `504` rather than a client timeout;
- `Browser:DisableDevShmUsage: true`, for containers.

It leaves `Browser:NoSandbox` at its default, `false`: Chromium runs with its sandbox, which needs
the seccomp profile [`deploy/seccomp/chromium.json`](../../deploy/seccomp/README.md). Images before
this change set it to `true`. `ReportsEngine__Browser__NoSandbox=true` opts out, only for trusted HTML
or on a platform that cannot permit unprivileged user namespaces.

Authenticated admission defaults to 10 MiB per request and four in-flight requests per caller, with a 90-second whole-request deadline. See [caller limits](../security.md#admission-and-deadlines). A tighter Kestrel limit still applies; the `Kestrel:Limits` section binds as usual:

- `Kestrel__Limits__MaxRequestBodySize`, in bytes, caps the transport request body (30,000,000 by Kestrel default); admission applies its own tighter 10 MiB default. A larger body is a `413` with problem details.
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

Worker mode retains HTTP telemetry in the API. The private worker does not inherit OTLP
credentials or export engine telemetry through the gateway. Worker stderr is drained without
logging document-controlled diagnostics. Full worker lifecycle telemetry is a production
acceptance gate for the experiment.

To look at it locally, start the [standalone Aspire
dashboard](https://learn.microsoft.com/dotnet/aspire/fundamentals/dashboard/standalone) (it takes
OTLP/gRPC on port 18889) or any OpenTelemetry Collector, and point the server at it:

```bash
docker run --rm -p 127.0.0.1:8080:8080 --env-file .reports-secrets/server.env \
  --security-opt seccomp=deploy/seccomp/chromium.json \
  -e OTEL_EXPORTER_OTLP_ENDPOINT=http://host.docker.internal:18889 \
  atli-reports-server
```

In this repository, `aspire start` runs the server under `examples/Atli.Reports.AppHost` with the
export already wired to the AppHost's dashboard.

## Container image

```bash
# Once the image is released; until then build it from this checkout. The seccomp profile comes
# from the same release.
curl -fsSLO https://raw.githubusercontent.com/atlitech/reports/0.26.0/deploy/seccomp/chromium.json
docker run --rm -p 127.0.0.1:8080:8080 --env-file .reports-secrets/server.env \
  --security-opt seccomp=chromium.json \
  ghcr.io/atlitech/reports-server:0.26.0
```

- **Build and runtime share Ubuntu 24.04** (`sdk:10.0-noble-aot`, which carries the NativeAOT
  toolchain, and `runtime-deps:10.0-noble`), so the NativeAOT binary runs against the glibc it was
  linked with. Both are pinned by the digest of their multi-arch index, and Dependabot proposes
  new digests weekly.
- **The browser is `chrome-headless-shell`** from Chrome for Testing (`linux64` or `linux-arm64`),
  which renders and isolates conversions several times faster than the full browser (see
  [architecture.md](architecture.md#isolation)). Its version is pinned; see
  [The Chrome version](#the-chrome-version). The image build fails if the browser links a shared
  library the image does not install (the `linux64` and `linux-arm64` builds link different sets).
- **`tini` is PID 1.** It forwards `SIGTERM` to the server, whose shutdown drains conversions and
  closes the browser cleanly, and it reaps the browser's exited child processes so none linger as
  zombies.
- **The server runs as the non-root `app` user** (UID 1654, set by number so Kubernetes'
  `runAsNonRoot` can verify it).
- **The browser runs with Chromium's sandbox**, which needs the seccomp profile
  [`deploy/seccomp/chromium.json`](../../deploy/seccomp/README.md): Docker's default profile plus the
  user namespaces the sandbox creates. Without it the browser cannot start, and readiness stays
  `503` with the reason in `/health/details`. See [Chromium's sandbox](../security.md#chromiums-sandbox).

### The Chrome version

The `CHROME_VERSION` build argument's default in
[`src/Atli.Reports.Server/Dockerfile`](../../src/Atli.Reports.Server/Dockerfile), such as
`ARG CHROME_VERSION=154.0.8037.92`, is the one place the version is set: local builds, pull request
checks, and releases all use it, so an image built from a given commit always has the same browser.

- **Updates come as pull requests.** A daily workflow
  ([`chrome-headless-shell-bump.yml`](../../.github/workflows/chrome-headless-shell-bump.yml))
  compares the pin with Chrome for Testing's stable channel. When stable is newer and both
  `linux64` and `linux-arm64` downloads exist, it opens (or updates) a pull request with the new
  pin from the `automation/chrome-headless-shell` branch. It never proposes an older version. It
  writes through a GitHub App, so the pull request's checks run; the comment at the top of the
  workflow describes the one-time setup (an App with Contents and Pull requests write access,
  the `AUTOMATION_APP_CLIENT_ID` variable, and the `AUTOMATION_APP_PRIVATE_KEY` secret).
- **Merging a new pin refreshes the published image.**
  [`server-image-refresh.yml`](../../.github/workflows/server-image-refresh.yml) rebuilds the most
  recently published release from its own commit with the new browser, smoke-tests both
  architectures, and moves that release's tags to it (see [Published image](#published-image)).
  Older releases are not rebuilt; it can also be run by hand for a given release.

### Published image

Each release publishes `ghcr.io/atlitech/reports-server` for `linux/amd64` and `linux/arm64`. Both
are built natively (not emulated) with the Chrome version the release's Dockerfile pins, and each
architecture is smoke-tested and checked for that Chrome version before any tag moves.

| Tag | Points at |
| --- | --- |
| `<version>-chrome<chrome>`, for example `0.26.0-chrome154.0.8037.92` | That release with that Chrome version. It never moves. |
| `<version>`, for example `0.26.0` | That release, with the newest Chrome it was built with. It moves when a Chrome refresh rebuilds the release. |
| `<major>.<minor>`, for example `0.26` | The most recently published release of that line. A version with a pre-release suffix, such as `0.26.0-preview.1`, does not move it. |
| `latest` | The most recently published release that is not a pre-release, by version or by its GitHub release. |

A refresh moves `<version>`, adds `<version>-chrome<new chrome>`, and moves `<major>.<minor>` and
`latest` only if they still point at that release's image. It never moves a tag to an older Chrome.
Pin `<version>-chrome<chrome>` (or a digest) when the image must not change at all; pin `<version>`
to get browser security fixes for that release on the next pull.

Each image carries OCI labels (`source`, `version`, `revision`, `licenses`, and
`io.github.atlitech.reports.chrome-version`), an SBOM, and a BuildKit provenance attestation that
records the build arguments, Chrome's version among them:

```bash
docker buildx imagetools inspect ghcr.io/atlitech/reports-server:0.26.0 --format '{{ json .SBOM }}'
docker buildx imagetools inspect ghcr.io/atlitech/reports-server:0.26.0 --format '{{ json .Provenance }}'
```

The multi-arch index also has a GitHub artifact attestation (SLSA build provenance, signed through
Sigstore) from this repository's image workflow,
[`server-image-publish.yml`](../../.github/workflows/server-image-publish.yml), as called by the
release workflow or, for a refreshed image, by the refresh workflow on `main`:

```bash
gh attestation verify oci://ghcr.io/atlitech/reports-server:0.26.0 --repo atlitech/reports
```

The attested subject is the multi-arch index, so verify a tag or the index digest
(`oci://ghcr.io/atlitech/reports-server@sha256:...`), not the per-platform digest that `docker pull`
resolves on one machine.

### Building the image

```bash
docker build -f src/Atli.Reports.Server/Dockerfile -t atli-reports-server .
scripts/create-reports-api-key.sh
docker run -p 127.0.0.1:8080:8080 --env-file .reports-secrets/server.env \
  --security-opt seccomp=deploy/seccomp/chromium.json atli-reports-server
```

That builds the pinned Chrome version. To try another one, pass it as a build argument: a version
number, or `stable` for Chrome for Testing's current stable release. `stable` is looked up when the
browser layer is built, so `--no-cache-filter browser` makes Docker look it up again instead of
reusing an earlier download:

```bash
docker build -f src/Atli.Reports.Server/Dockerfile -t atli-reports-server \
  --build-arg CHROME_VERSION=stable --no-cache-filter browser .
docker run --rm --entrypoint /opt/chrome-headless-shell/chrome-headless-shell atli-reports-server --version
```

`.github/scripts/smoke-test-server-image.sh atli-reports-server` runs the checks CI runs on every
image change: under the seccomp profile, the server becomes ready, rejects anonymous conversions,
accepts authenticated conversion and OpenAPI requests, runs as a non-root user under `tini` with a
read-only root filesystem, omits the application-secret canary from readable browser environments,
runs Chromium with its sandbox (no `--no-sandbox`, renderers outside the browser's user namespace), and
shuts down cleanly; under Docker's default profile, it fails closed with the remedy in
`/health/details` and its log. CI also validates JWT authentication in the NativeAOT image and exercises the Kubernetes
deployment in a disposable cluster. See [validation scope](../security.md#validation).

### Throughput

Measured with 50 one-page documents against a container limited to 2 CPUs and 2 GB (Linux arm64):

| | Sequential | 8 concurrent clients | Left in `/tmp` |
| --- | --- | --- | --- |
| Before (a browser per request, Debian Chromium) | 3.0 docs/s, p50 315 ms | 2.2 docs/s, p50 3.5 s | 187 entries after 101 requests |
| After (shared browser, isolated contexts, `chrome-headless-shell`) | 58.7 docs/s, p50 13 ms | 57.6 docs/s, p50 121 ms | the live profile only |
