# Atli Reports Server

`Atli.Reports.Server` is a small NativeAOT HTTP service over `Atli.Reports.Engine`. It ships as a
container image. Configure authentication before starting it; see [Security and production deployment](../security.md). The default document network policy rejects external assets.

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

In gateway mode with `Provisioning:Mode=OnDemand` it also describes `DELETE /tenants/{tenantId}`
(see [Gateway mode](#gateway-mode)). Its `info.version` is the server's release. The `/health`
endpoints are left out: they serve probes, not clients.

## Health

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

## Gateway mode

`ReportsServer:Mode=Gateway` makes the server the shared API of the
[hosted renderer design](../hosted-renderers.md). It authenticates callers as usual, resolves each
conversion's product tenant from the caller's identity, and relays the conversion to that tenant's
own renderer: this server in integrated mode, admitting only the gateway's credential for it. The
gateway registers no engine and starts no browser. `POST /convert` and its problem details,
authentication, caller admission and deadline, OpenAPI, `/health/*`, and telemetry work as described
above, so clients do not change. `Integrated`, the default, is everything else on this page; any
other mode fails at startup. Gateway mode also fails at startup under `Authentication:Mode=None`,
which makes every caller `anonymous`, unless `AllowAnonymousCallers` is set for development.

The settings bind once from `ReportsServer:Gateway` and are validated at startup, like the
security settings:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Tenants:<n>:CallerId`, `Tenants:<n>:Tenants:<m>`, `Tenants:<n>:TenantPrefixes:<m>` | Required | Each authenticated caller ID (an API key's `CallerId`, the JWT caller claim, or `anonymous` under `Authentication:Mode=None` with `AllowAnonymousCallers`) and its product tenants: those it lists in `Tenants`, 1 to 63 lowercase letters, digits, and hyphens, and every valid tenant ID under one of its `TenantPrefixes`, such as `myapp-3f2504e0-4f89-11d3-9a0c-0305e82c3301` under `myapp-`. A prefix is 2 to 27 lowercase letters, digits, and hyphens, starting with a letter or digit and ending with a hyphen, so `acme-` does not own `acmecorp-1`. Each caller needs a tenant or a prefix. Callers may share listed tenants, but not the tenants under a prefix: no two callers' prefixes may overlap (one starting with the other). No caller may list a tenant under any prefix, its own included: the provisioning service retires and deletes the renderers of a prefix's tenants, and the gateway never has it create a listed tenant's, so such a tenant would lose its renderer for good. Renderer records do not name the caller whose prefix a tenant is under, so a prefix moved to another caller gives that caller every renderer under it, with its memory snapshot. Do not assign a prefix to another caller while tenants remain under it; delete them first. `readiness-probe` is reserved for readiness (below), here and in `Records:Renderers`, and no prefix may own it |
| `TenantHeader` | `X-Reports-Tenant` | The header a caller names its tenant in; only a caller with one listed tenant and no prefix may leave it out |
| `Records:Store` | Required | Where renderer records come from: `Configuration` (the `Renderers` below, read-only), `File` (`Records:Path`), or `KeyVault` (`Records:VaultUri`, and `Records:ManagedIdentityClientId` for a user-assigned identity) |
| `Records:Renderers:<n>:TenantId`, `Url`, `ApiKey`, `SandboxId`, `MaxConcurrentRequests` | | `Configuration` only: each tenant's renderer, the credential the gateway presents to it, its sandbox ID if it is a sandbox, and how many requests it admits at once |
| `Records:CacheDuration` | `00:00:30` | How long a record lookup is reused; a missing record is reused for 5 seconds at most, and a record is dropped sooner when its renderer rejects the gateway's credential, cannot be reached, or names a sandbox that no longer exists |
| `Wake:Mode` | `None` | `Sandboxes` resumes suspended Azure Container Apps sandboxes whose port does not wake them on request; the provisioner's ports do (`OnDemand`), so they need `None` |
| `Wake:Sandboxes:SubscriptionId`, `ResourceGroup`, `SandboxGroup`, `Region`, `ManagedIdentityClientId` | | The renderers' sandbox group, for `Sandboxes` |
| `Wake:Timeout` | `00:00:30` | How long one request keeps resuming and resending to a renderer that is not running |
| `Provisioning:Mode` | `None` | `OnDemand` has the provisioning service create the renderer of a tenant under a caller's prefix on first use, and maps `DELETE /tenants/{tenantId}` (below). It needs a record store the service writes, `File` or `KeyVault` |
| `Provisioning:Url` | | For `OnDemand`: the provisioning service's `https` address, on internal ingress. `http` needs `AllowHttpRenderers`, and sends the gateway's key for the service in clear |
| `Provisioning:ApiKey` | | For `OnDemand`: the gateway's credential for the service, `<id>.<secret>` |
| `Provisioning:Timeout` | `00:01:00` | How long the gateway waits for the service to create or delete a renderer; a conversion waits no longer than its `RendererTimeout` |
| `Provisioning:MaxConcurrentDeletesPerCaller` | `2` | How many deletions (`DELETE /tenants/{tenantId}`, below) one caller may have in flight in this replica, 1 to 100; one more is `503` `Busy` |
| `RendererTimeout` | `00:01:30` | The deadline of one forwarded conversion, from the record lookup to the PDF's last byte |
| `MaxPdfBytes` | `268435456` (256 MiB) | The largest PDF the gateway relays |
| `MaxConcurrentRequestsPerTenant` | `8` | Conversions in flight per tenant in this replica, across its callers; lower when this replica's share of what the tenant's renderer admits is fewer |
| `Replicas` | `1` | How many gateway replicas send conversions to the same renderers, 1 to 1000: each replica admits a tenant its record's `MaxConcurrentRequests` divided by this, rounded down and at least 1. Set it to the most replicas that run at once |
| `MaxNewTenantLookupsPerCallerPerSecond` | `20` | How many record lookups per second one caller may cause in this replica for tenants under its prefixes that the gateway has no answer for, 1 to 10000; see "New tenants have a budget" below |
| `AllowHttpRenderers` | `false` | Allows `http` renderer and provisioning service URLs, over which the gateway's credentials for them travel in clear. For tests and development only |
| `AllowAnonymousCallers` | `false` | Allows `Authentication:Mode=None`, under which anyone who reaches the gateway converts as `anonymous` for that caller ID's tenants. For tests and development only |

```text
ReportsServer__Mode=Gateway
ReportsServer__Gateway__Tenants__0__CallerId=billing-app
ReportsServer__Gateway__Tenants__0__Tenants__0=contoso
ReportsServer__Gateway__Records__Store=KeyVault
ReportsServer__Gateway__Records__VaultUri=https://reports-renderers.vault.azure.net/
ReportsServer__Gateway__Wake__Mode=Sandboxes
ReportsServer__Gateway__Wake__Sandboxes__SubscriptionId=<subscription-id>
ReportsServer__Gateway__Wake__Sandboxes__ResourceGroup=reports
ReportsServer__Gateway__Wake__Sandboxes__SandboxGroup=renderers
ReportsServer__Gateway__Wake__Sandboxes__Region=eastus2
```

- **The tenant comes from the caller's identity, never from the body.** A caller with no tenant
  gets the authorization `403` (`Forbidden`). A caller with one listed tenant and no prefix needs
  no header, and a header that names another tenant is a `403`. Any other caller, with several
  tenants or a prefix, must name one in `TenantHeader`: a missing header (or the header sent
  twice) is a `400` (`InvalidRequest`), and a tenant it does not belong to is a `403`. The header
  names one of the caller's tenants when the caller lists it, or when one of its prefixes owns it:
  a valid tenant ID that starts with the prefix and continues past it. The prefix alone, a tenant
  under another caller's prefix, and an invalid tenant ID are all `403`s. An empty header counts
  as a missing one, so it selects a single-tenant caller's tenant and is a `400` for any other
  caller. The gateway lets a caller use every tenant under its prefixes and knows nothing of the
  caller's own users, so an application that gives each of its workspaces a tenant must check
  that the signed-in user may use a workspace before naming the workspace's tenant. This runs
  after caller admission and before the body is read. A tenant's in-flight limit is `MaxConcurrentRequestsPerTenant`,
  or this replica's share of its record's `MaxConcurrentRequests` (the requests its renderer
  admits) when that is lower: divided by `Replicas`, rounded down, and at least 1. A tenant at its
  limit gets `503` `Busy` with `Retry-After: 1`, and nothing queues in the gateway. The limit
  holds per replica, which knows nothing of the others' traffic, so `Replicas` must count every
  replica that may run: in a [measured run](../../benchmarks/results/2026-10-04-6cdce25-hosted-renderers-production-amd64.md)
  at twice the admitted concurrency, two replicas each admitting the whole of it made renderers
  refuse 8,031 requests in 5 minutes, which the gateway resent, and 388 conversions still ended
  in `Busy`. Set to the most replicas of an autoscaled gateway, it leaves renderers partly unused
  while fewer run; with more replicas than a renderer admits, each still admits 1, and the
  renderer's `Busy` is retried as described below. Caller admission
  (`Limits:MaxConcurrentRequestsPerCaller`, 4 by default) applies before the tenant limits, so a
  caller that serves several tenants needs it raised to their combined limits.
- **New tenants have a budget.** A caller with a prefix can name a tenant the gateway has not
  seen on every request, and each such request reads the record store before its body is read,
  and with `Provisioning:Mode=OnDemand` has the provisioning service read the store and create a
  renderer. Key Vault throttles a vault at about 4,000 operations per 10 seconds, 400 a second,
  and once one caller spends that, every tenant's record reads fail. So each caller may start at
  most `MaxNewTenantLookupsPerCallerPerSecond` lookups a second in a replica, with a burst of as
  many, for tenants under its prefixes whose answer the gateway does not have: neither cached (a
  record or none) nor being read for another request. One more is `503` `Busy` with
  `Retry-After: 1`, and reads nothing. Cached answers cost nothing, and listed tenants are never
  limited. The budget holds per replica, so size it against the vault's limit with every replica
  and every caller with a prefix counted: a new tenant costs up to about four vault operations
  (the gateway's lookup, the service's lookup and its writing of the record, and the gateway's
  reading of it), so the budget times the replicas times those callers times four must stay well
  under 400 a second, with room left for the vault's other traffic. The default, 20, lets one
  caller on two replicas cause about 160 operations a second.
- **Forwarding.** The gateway validates the request as above, looks up the tenant's renderer
  record, and posts the conversion to `{Url}/convert` in `Atli.Reports.Client`'s wire format, with
  the record's credential in `X-Reports-Api-Key`. Nothing of the caller's request goes along: no
  headers, credentials, cookies, or trace context. Redirects are not followed. A tenant without a
  record, a record store that fails, and a renderer that cannot be reached (a refused or reset
  connection, or one that does not open within 10 seconds, TLS handshake included) are `503`
  `BrowserUnavailable`. A record the store cannot read fails only its own tenant's conversions.
- **Resending.** A conversion is sent again only while its renderer wakes (below) or is busy. A
  renderer's `429`, or a `503` with the kind `Busy`, is resent up to 8 times after a random pause
  of 100 to 500 ms each, within `RendererTimeout`, so a burst beyond the renderer's concurrency
  (or every request waiting for a wake, resending at once) spreads out instead of failing. After
  the last one the caller gets `503` `Busy`. No other answer is resent.
- **The renderer's answer is untrusted.** A `200` counts only as `application/pdf` with framed
  length (chunked or `Content-Length`) whose first bytes are `%PDF-`, checked before the caller's
  response starts; otherwise the caller gets `500` `RenderFailed`. The PDF then streams through
  with a byte count. A PDF over `MaxPdfBytes`, a body cut short or reset, and `RendererTimeout`
  after the first byte abort the caller's connection, as any failure after the first byte does.
  Before it, `RendererTimeout` is a `504` `Timeout`.
- **Renderer errors.** The gateway reads at most 16 KiB of an error body. A problem's `kind` is
  kept if it names a `ConversionErrorKind` other than `Canceled` exactly, and the gateway's own
  status and `Retry-After` follow from it. Only `InvalidRequest`, `SignalTimeout`, and
  `PolicyDenied` pass the renderer's `detail` on, without control characters and cut to 512
  characters; every other kind gets the gateway's fixed message. A member whose text is not valid
  (a lone surrogate escape, invalid UTF-8) counts as missing. A renderer `401`, or a `403` problem,
  means the gateway's credential is wrong: the caller gets `503` `BrowserUnavailable`, and the
  gateway logs an error. A renderer `413` is the caller's `400` `InvalidRequest` with a fixed
  message: the document is larger than the renderer accepts once the gateway has encoded it. The
  wire format escapes characters outside the Basic Multilingual Plane, so an emoji's 4 UTF-8 bytes
  take 12, and a body under the gateway's own limit can exceed the renderer's. Two answers of the
  Sandboxes port proxy (plain JSON, never the renderer's problem details) are `503`
  `BrowserUnavailable` too: `404 {"error":"Not found"}`, a renderer deleted or replaced since the
  gateway read its record, which the next conversion reads again; and `403` with the `errorCode`
  `IpAccessDenied`, a port whose allow-list does not admit the gateway's address, which the gateway
  logs as an error. Other answers are `Busy` for `429`, `BrowserUnavailable` for `5xx`, and
  `RenderFailed` otherwise.
- **Waking renderers.** A renderer port with on-demand activation, which the
  [provisioner](../../src/Atli.Reports.Provisioner/README.md) creates by default, wakes its
  suspended sandbox on the conversion request itself, so the gateway needs no wake settings and no
  rights over sandboxes. For ports without it: with `Wake:Mode=Sandboxes` and a record with a
  `SandboxId`, the platform's
  `403 {"error":"Sandbox is not running"}` (not the renderer's own problem details) makes the
  gateway wake the sandbox and send the conversion again. That answer reaches the gateway through
  the renderer's port, so a compromised renderer can send it too: before resuming, the gateway
  reads the sandbox's state from the Sandboxes data plane. A sandbox reported `Running` means the
  answer did not come from the platform, which the caller gets as `503` `BrowserUnavailable` with
  no resume and no resend; a sandbox that no longer exists is `503` `BrowserUnavailable` too, and
  so, at once and with no resume, is a sandbox the data plane reports stopped because it was
  disabled (the provisioner's kill switch), which the platform would not resume until it is
  enabled.
  Concurrent requests share one state read and one resume per sandbox, a state read is reused for
  2 seconds, and a sandbox is resumed at most once every 5 seconds. Within `Wake:Timeout` the
  gateway also resends after `502`, `503` (other than `Busy`), a renderer it cannot reach, a
  failed state read or resume, and not-running answers while the sandbox is still not running,
  waiting 250 ms and up to 1 s between them; after it the caller gets `503` `BrowserUnavailable`.
  The gateway's identity needs only `sandboxes/read` and `sandboxes/resume/action` on the group.
- **Creating renderers on demand.** With `Provisioning:Mode=OnDemand`, a tenant under one of its
  caller's prefixes gets its renderer from the
  [provisioning service](../hosted-renderers.md#applications-with-many-tenants). When the tenant
  has no record, the port proxy's `404 {"error":"Not found"}` says its renderer is gone, or (with
  `Wake:Mode=Sandboxes`) the data plane no longer knows the record's sandbox, the gateway asks the
  service to ensure one (`PUT /tenants/{tenantId}/renderer`), reads the record
  again, and sends the conversion to it. That happens at most once per conversion; a conversion
  that still cannot be sent gets the answer described above. The concurrent conversions of a
  tenant in a replica share one call, which carries none of their trace context and which no
  single conversion's cancellation stops. `Provisioning:Timeout` bounds the call, and each
  conversion waits for it only within its `RendererTimeout`, after which it is a `504` `Timeout`.
  The first conversion of a new tenant waits for its renderer to be created. Listed tenants are
  never created this way: one without a record stays `503` `BrowserUnavailable`. The service's
  refusals reach the caller as the gateway's own errors, never in the service's words:
  `NotAllowed`, which means that the gateway's and the service's prefixes disagree, is `503`
  `BrowserUnavailable` and logged as an error; `QuotaExceeded` is `503` `BrowserUnavailable` with a
  message that the prefix's renderer quota is full, until tenants are deleted or retire;
  `RateLimited` is `503` `Busy` with the service's `Retry-After`, kept between 1 and 60 seconds;
  `Disabled`, a renderer the operator disabled, is `503` `BrowserUnavailable`; and `Failed`, any
  other answer, a service that cannot be reached, and `Provisioning:Timeout` are `503`
  `BrowserUnavailable`. The gateway's client of the service follows no redirects and reads at most
  16 KiB of an answer.
- **Deleting tenants.** With `Provisioning:Mode=OnDemand`, `DELETE /tenants/{tenantId}` deletes
  the renderer of a tenant under one of the caller's prefixes, with its record and memory
  snapshot, through the service's `DELETE /tenants/{tenantId}/renderer`, and answers `204`, also
  for a tenant that has none. It needs the permission `reports.tenants` (see
  [Authentication](../security.md#authentication)). A listed tenant is a `403`, since the operator
  manages it, and so is a tenant that is not the caller's; an invalid tenant ID is a `400`
  `InvalidRequest`. The service's refusals reach the caller as the gateway's own errors, never in
  the service's words. A renderer the operator disabled (`Disabled`) is a `409` (`InvalidRequest`)
  with a fixed message: the service neither deletes nor replaces it until the operator enables or
  deletes it, and the gateway keeps the tenant's cached record. `NotAllowed`, `Failed`, any other
  answer, a service that cannot be reached, and `Provisioning:Timeout` are `503`
  `BrowserUnavailable`; retrying is safe, since deleting a tenant without a renderer succeeds. The
  tenant header, caller admission, and tenant admission take no part. Instead, a caller may have
  `Provisioning:MaxConcurrentDeletesPerCaller` deletions in flight in a replica, each waiting for
  the service for up to `Provisioning:Timeout`; one more is `503` `Busy` with `Retry-After: 1`,
  and does not reach the service. The replica that serves the deletion forgets the tenant's
  record, and no lookup that started before the deletion brings it back, but other replicas may
  route the tenant's conversions to the deleted renderer for up to `Records:CacheDuration`; such a
  conversion gets the proxy's `404` and creates a new renderer. An application must therefore
  stop converting for a tenant before deleting it. The OpenAPI document describes the route;
  without `OnDemand` it does not exist.
- **Readiness.** `/health/ready` has one check, `renderer_records`: always healthy for the
  `Configuration` store, and for the others healthy when the store answered a lookup of the
  reserved tenant `readiness-probe` within the last 30 seconds. Finding nothing there, or
  something unreadable, is an answer; only a store that fails or takes over 10 seconds is
  unhealthy. No tenant's record is read, so one damaged record never makes a replica unready.
  `/health/live` is unchanged.
- **Logs and traces.** The gateway logs tenant and sandbox IDs, never HTML, PDFs, credentials, or
  tokens: events 40 to 51 for forwarding (44, an error, is a rejected credential), 52 and 53 for
  refused tenants and full tenants (a line per refused request: about 16 a second for six tenants
  at twice their limits), 54 for a not-running answer from a running sandbox, 55 for a
  record that names a sandbox that does not exist, 56 (at `Debug`) for a resend to a busy
  renderer, 57 for a renderer the port proxy does not find, 58 (an error) for a port that refuses
  the gateway's address, 59 for a disabled sandbox, 60 to 63 for state reads and resumes, 64 to
  67 for the provisioning service (64 when the gateway asks for a renderer, 65 when the service
  created or found it, with the time it took, 66 for a refusal, at `Error` for `NotAllowed` and
  `Warning` otherwise, and 67, an error, for a failure), 68 and 69 for a deleted tenant and a
  refused deletion, and 70 for a caller over its budget of new tenants. The request span carries
  the tenant as `atli.reports.tenant`. Record lookups, sandbox checks, and renderer creations are
  shared by the requests waiting for them, so they run without any request's trace context, and
  neither the renderer client, the Sandboxes client, nor the provisioning client sends trace
  headers or baggage.

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
