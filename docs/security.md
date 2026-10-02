# Security and production deployment

Atli Reports is a self-hosted conversion service for application-owned templates and controlled
assets. The server authenticates calling applications, authorizes conversions, limits admitted
work, and restricts document networking. These controls do not make the shared browser a sandbox
for arbitrary hostile HTML. The shipped container runs Chromium with its own sandbox, which needs
the seccomp profile in [`deploy/seccomp`](../deploy/seccomp/README.md); see
[Chromium's sandbox](#chromiums-sandbox). Run it without application secrets, cloud permissions, or
access to sensitive services.

The customer application authorizes the end user's access to business records before generating
HTML. Atli authorizes that application's conversion request. Bearer tokens, API keys, and tenant
headers are never copied into document content or asset requests. A request's tenant header or
body cannot select a different authenticated caller or quota.

## Upgrading an existing deployment

This changes the standalone server's defaults. Before rolling out the image, configure an explicit
authentication mode and deploy matching credentials to clients. Existing anonymous installations
must deliberately choose `None` to retain that behavior. Remote assets are disabled by default;
embed assets in the document or configure exact public asset origins with `AllowList`.

Update monitoring to read the anonymous status-only probe endpoints. Detailed health and OpenAPI
now require a diagnostics credential. Clients should handle `401` and `403` without retrying,
`429` with bounded backoff, and `422` with the `PolicyDenied` kind by correcting document policy.
Readiness, scaling, and caller limits remain local to each replica.

The server image now runs Chromium with its sandbox; earlier images set
`ReportsEngine:Browser:NoSandbox=true`. Run the container with
[`deploy/seccomp/chromium.json`](../deploy/seccomp/README.md):
`docker run --security-opt seccomp=deploy/seccomp/chromium.json`, Compose `security_opt`, or a
Kubernetes `Localhost` profile. Without it the browser cannot start: `/health/ready` stays `503`,
conversions fail with `BrowserUnavailable`, and `/health/details` and log event 301 say what to do.
The server never falls back to running without the sandbox. Opt out explicitly with
`ReportsEngine__Browser__NoSandbox=true` only for trusted HTML, or on a platform that cannot permit
unprivileged user namespaces.

## Authentication

The standalone server refuses to start until `ReportsServer:Authentication:Mode` is explicitly set
to `ApiKey`, `JwtBearer`, or `None`. This also applies in Development. Configuration is validated
at startup; key changes, revocation, and policy changes require a restart or new revision. The
engine and embedded Blazor libraries do not acquire an authentication dependency.

| Endpoint | Required permission |
| --- | --- |
| `POST /convert` | `reports.convert` (configurable JWT permission) |
| `GET /openapi/v1.json` | `reports.diagnostics` |
| `GET /health/details` | `reports.diagnostics` |
| `GET /health/live`, `GET /health/ready` | Anonymous, status only |

`None` explicitly permits anonymous access to every endpoint, including diagnostics. Use it only
on a controlled local endpoint or behind a separately enforced security boundary. A private
address alone does not authenticate callers. Proxies must not expose an alternate route around
authorization. No forwarded identity or tenant headers are trusted by this server.

### API keys

From the repository root:

```bash
scripts/create-reports-api-key.sh
docker compose -f src/Atli.Reports.Server/docker-compose.yml up --build
curl --config .reports-secrets/client.curl http://localhost:8080/convert \
  -H 'Content-Type: application/json' \
  -d '{"html":"<!doctype html><h1>Hello, PDF</h1>"}' --output hello.pdf
```

The script writes owner-only files into the ignored `.reports-secrets` directory. `server.env`
contains the verifier; `client.env` and `client.curl` contain the credential and must remain private.
The script refuses to overwrite existing credentials. This local example grants conversion and
diagnostics; remove `Permissions__1` from the server file for production application callers that
do not need diagnostics. Give operators their own diagnostics-only credential.

The HTTP header is `X-Reports-Api-Key: <id>.<secret>`. Generate at least 32 cryptographically random
secret bytes (the script encodes 32 bytes as 64 hex characters). Configure one entry per key:

```text
ReportsServer__Authentication__Mode=ApiKey
ReportsServer__Authentication__ApiKeys__0__Id=<id>
ReportsServer__Authentication__ApiKeys__0__Hash=<base64 SHA-256 of the complete id.secret>
ReportsServer__Authentication__ApiKeys__0__CallerId=billing-app
ReportsServer__Authentication__ApiKeys__0__Permissions__0=reports.convert
ReportsServer__Authentication__ApiKeys__0__ExpiresAt=2027-01-01T00:00:00Z
```

The server stores no raw API key and compares verifiers in constant time. Send credentials only
over TLS or an explicitly trusted local/mesh transport. Do not put them in URLs, image layers,
checked-in appsettings, or command-line arguments. Rotation uses two distinct key IDs mapped to
the same CallerId: deploy both verifiers, switch clients, then remove or disable the old entry and
roll every replica. Both keys share the caller's local concurrency limit.

The client reads `ReportsClient:ApiKey`, including `ReportsClient__ApiKey`. Its default HTTP
transport does not follow redirects, preventing the custom credential header from reaching a
redirect destination. Custom HTTP handlers must preserve this behavior. Health probes carry no
conversion credential.

### Workload access tokens

```text
ReportsServer__Authentication__Mode=JwtBearer
ReportsServer__Authentication__Jwt__Authority=https://login.microsoftonline.com/<directory-id>/v2.0
ReportsServer__Authentication__Jwt__Audience=<reports-api-audience>
ReportsServer__Authentication__Jwt__PermissionClaimType=roles
ReportsServer__Authentication__Jwt__RequiredPermission=reports.convert
ReportsServer__Authentication__Jwt__CallerIdClaimType=sub
```

The standard ASP.NET Core JWT bearer handler validates the issuer, audience, signing key,
signature, expiration, and lifetime (30 seconds of clock tolerance), using HTTPS metadata and
signing-key discovery. The caller must also have the required permission. A valid token without
that permission gets `403`; a missing or invalid credential gets `401`, never a login redirect.
Use an identity-provider-specific stable application claim if `sub` is unsuitable. Internal
Atli identity claims in a supplied token are discarded and rebuilt after validation.

JWT requests identify quota partitions by validated issuer and caller claim. `Callers` overrides
match the caller claim within the configured authority. Identity-provider tenants and future
Atli product tenants are different concepts; a header cannot create a product-tenant entitlement.

The caller can obtain access tokens with its existing credential library and provide them through
the client's asynchronous token provider. Atli does not issue tokens or require an Azure SDK.
Authentication failure and forbidden access are distinct `ConversionErrorKind` values and are not
conversion retries. See the [client guide](../src/Atli.Reports.Client/README.md) for the API.

For Azure, assign the calling application's managed identity the Reports API application role;
configure the API audience and validate the role. ACA authentication alone does not enforce the
application role. For Kubernetes, configure a trusted issuer/federation arrangement or use API
keys; a service account is not automatically an identity recognized by this API. The renderer
does not need a managed identity or a mounted Kubernetes service-account token.

## Document networking

The server defaults to `ReportsEngine:Network:Mode=Disabled`. Inline HTML, CSS, JavaScript, and
data URI assets still work. Intercepted external requests and unsupported browser channels fail
the conversion with `PolicyDenied` (`422`). Chromium can reject some URLs, such as local files,
before an observable request occurs; those assets remain absent from an otherwise successful PDF.
A conversion request cannot override the host's network policy.

| Mode | Behavior |
| --- | --- |
| `Disabled` | Reject external document requests. Prefer self-contained reports. |
| `AllowList` | Fetch approved public HTTP(S) assets through a bounded broker. |
| `Unrestricted` | Browser networking follows the host; explicit opt-in on the server. |

The embedded engine retains `Unrestricted` as its compatibility default. Set a restricted mode
explicitly when embedding. Remote mode uses the server's settings; configuring the idle engine
inside a client application does not change the report server's policy.

```text
ReportsEngine__Network__Mode=AllowList
ReportsEngine__Network__AllowedOrigins__0=https://assets.example.com
ReportsEngine__Network__MaxRequests=100
ReportsEngine__Network__MaxResponseBytes=10485760
ReportsEngine__Network__MaxTotalResponseBytes=52428800
ReportsEngine__Network__RequestTimeout=00:00:15
```

Origins are exact scheme, hostname, and port matches, with no wildcards, credentials, or path
rules. Only GET/HEAD assets are fetched. Remote document/frame navigation is denied. Redirect
destinations are reauthorized, all DNS answers must be public, and connections use the validated
numeric address without a second DNS lookup. The broker ignores ambient HTTP proxies and sends
no document cookies or authorization headers. Per-asset decoded bytes, total bytes, request count
(including redirects), and duration are bounded. Browser worker networking and WebSockets are
unavailable in restricted modes. A deny-only browser proxy also blocks requests missed by the
page interceptor.

The broker's own errors and logs omit asset URLs. Applications embedding the engine must also
review their HTTP tracing configuration: host-installed `HttpClient` instrumentation can observe
asset URLs. Do not include secrets in asset URLs.

This broker is defense in depth for controlled documents. A browser exploit, unhandled browser
protocol, or direct networking outside these mechanisms must still be contained by deployment
egress controls. Do not advertise this as malicious-code containment. Disabling JavaScript or
sanitizing HTML alone does not prevent requests made by images, fonts, CSS, or frames.

The browser child process receives a small environment allowlist instead of application
configuration, proxy credentials, tokens, or cloud identity variables. Host-controlled
`ReportsEngine:Browser:EnvironmentVariables` can add a required browser setting; never add secrets.
Clearing environment variables does not revoke filesystem permissions or credentials accessible
through the surrounding container's identity endpoint.

## Chromium's sandbox

Chromium runs its renderers, the processes that parse HTML and run JavaScript, in user and network
namespaces apart from the browser's and each in a PID namespace of its own, confined to an empty
directory, under Chromium's own seccomp-bpf filter. A V8 or Blink exploit then lands in a process with no file system, no network, and a narrow
set of syscalls; to reach the server process, its DevTools connection, other conversions'
documents, or the container, it needs a second bug that escapes the sandbox. Without the sandbox,
one renderer bug runs code as the server's user in the container. The sandbox narrows the damage of
a browser bug; it still does not make the shared browser a boundary between hostile tenants (see
[Managed hosting boundary](#managed-hosting-boundary)).

The engine runs the sandbox unless `Browser:NoSandbox` is set, and the server image no longer sets
it. Creating the namespaces needs unprivileged user namespaces, which container runtimes deny by
default: Docker's default seccomp profile and containerd's `RuntimeDefault` deny the `clone`,
`unshare`, and `chroot` calls Chromium makes. [`deploy/seccomp/chromium.json`](../deploy/seccomp/README.md)
is Docker's default profile, pinned to a moby/profiles commit, plus exactly those calls, restricted
to user, PID, and network namespaces. Its README records the delta and how each part was found
necessary.

| Where | How |
| --- | --- |
| Docker | `docker run --security-opt seccomp=deploy/seccomp/chromium.json ...` |
| Docker Compose | `security_opt: ["seccomp=<path to deploy/seccomp/chromium.json>"]`, as in [`docker-compose.yml`](../src/Atli.Reports.Server/docker-compose.yml) |
| Kubernetes | A `Localhost` profile installed on every node; see [the example](../deploy/kubernetes/reports.yaml) |
| Aspire, local runs | `AddReportsServer` passes the profile to Docker or Podman |
| Aspire deployment targets | Configure the profile on the target, or opt out explicitly; see [the Aspire guide](aspire.md#deploy) |
| Azure Container Apps | No seccomp setting; [the template](../deploy/azure/reports.bicep) opts out explicitly. Whether its runtime permits user namespaces is unverified. |
| Isolated workers under gVisor | Chromium's own seccomp filter crashes there on arm64, so the [experiment](isolated-workers.md) keeps the worker's browser unsandboxed inside the gVisor boundary |

**AppArmor.** Ubuntu 23.10 and later (24.04 included) also restrict unprivileged user namespaces
through AppArmor (`kernel.apparmor_restrict_unprivileged_userns=1`), for processes AppArmor does not
confine. Docker's and containerd's default AppArmor profiles declare AppArmor ABI 3.0, which does
not mediate user namespaces, so by the kernel and profile sources, containers under them are not
affected; this has not been tested on such a host. Containers that run AppArmor-unconfined
(`--security-opt apparmor=unconfined`, privileged containers, pods in kind) are affected, as is
Chromium run directly on such a host, outside a container: allow it user namespaces with an AppArmor
profile, as [Chromium describes](https://chromium.googlesource.com/chromium/src/+/main/docs/security/apparmor-userns-restrictions.md),
or set the sysctl to `0`.

**When the sandbox cannot start, the server fails closed.** The browser exits at once, the engine
retries the launch in the background, and nothing converts. `/health/ready` answers `503`.
`/health/details` and the launch failure (log event 301, a `BrowserSandboxUnavailableException`)
start with what to do, before Chromium's own output ("No usable sandbox!"): use the seccomp profile,
check AppArmor, or opt out. Conversions fail with `BrowserUnavailable` and the same message.

**Opting out.** `ReportsEngine__Browser__NoSandbox=true` runs Chromium with `--no-sandbox`. Use it
only for trusted HTML, or on a platform that cannot permit unprivileged user namespaces, and keep the
rest of this page's containment.

**Cost.** In an exploratory comparison on 2026-10-02, with the isolated worker image (which ships the
same chrome-headless-shell) on OrbStack, arm64, one CPU per container, and five interleaved rounds,
a cold single-use conversion took about 30 ms longer with the sandbox (0.405 s against 0.376 s);
warm conversions differed within noise in both directions.

## Admission and deadlines

Authentication and authorization run before JSON body binding and conversion admission. Settings
bind from `ReportsServer`; environment variables use `__` separators.

| Setting | Default | Meaning |
| --- | --- | --- |
| `MaxConcurrentRequests` | 128 | All admitted conversions in this server process, uploads and streams included |
| `Limits:MaxConcurrentRequestsPerCaller` | 4 | Concurrent admitted requests per authenticated caller |
| `Limits:MaxRequestBodyBytes` | 10485760 | Request bytes; a tighter Kestrel limit still wins |
| `Limits:RequestTimeout` | `00:01:30` | Body reading, engine queue, rendering, and response streaming |

`Callers:0:CallerId` plus `Callers:0:Limits:*` overrides individual limits, inheriting unspecified
values from `Limits`. Headers and HTML cannot select a caller. A caller limit returns `429` with
`Retry-After: 1`; the server admission limit or
engine saturation returns `503`. Limits are per replica, not distributed quotas or billing limits.
The engine's tighter conversion timeout still applies. An expired admission deadline returns
`504` before response bytes, and aborts a response that already started.

The standalone browser drain is 70 seconds, `HostOptions:ShutdownTimeout` defaults to 80 seconds,
and the examples allow
90 seconds for container termination. Increase all relevant budgets together when allowing longer
reports. Account for body upload, retries, and outer ingress deadlines; ACA HTTP ingress has a
240-second request timeout. Do not promise durable job delivery from the in-memory engine queue.

## Deployment profiles

Aspire consumers use the [hosting integration](aspire.md) with explicit credentials and the
deployment environment their application uses. `WithDevelopmentApiKey()` generates run-only
credentials and refuses publishing. Production uses secret parameters with
`WithApiKeyAuthentication(...)` or explicit JWT settings. The authenticated dashboard test command
needs a supported credential helper; production JWT conversions use the application's token flow.

For teams deploying the ordinary container without Aspire, checked-in examples are available:

- [Kubernetes](../deploy/kubernetes/reports.yaml): two replicas, private ClusterIP service, explicit
  uppercase HTTP startup/readiness/liveness probes, disruption budget, read-only root, writable
  bounded `/tmp`, non-root execution, no capabilities, no service-account token, denied egress, and
  the `Localhost` seccomp profile Chromium's sandbox needs, which you install on every node first.
  Label authorized calling pods `reports-client=true` in the same namespace and supply the API key.
  The cluster CNI must actually enforce NetworkPolicy. The optional [HPA](../deploy/kubernetes/hpa.yaml)
  requires metrics-server and representative load testing.
- [Azure Container Apps](../deploy/azure/reports.bicep): an existing environment, internal ingress,
  API-key verifier secret, two minimum replicas, HTTP scaling, and explicit probes/deadlines. Image,
  environment, caller, and verifier are parameters. It provisions no broad managed identity.
  Container Apps cannot apply a seccomp profile, so the template opts out of Chromium's sandbox
  explicitly, which suits trusted HTML only.
  Add registry authentication and environment/network egress restrictions in your infrastructure;
  app-internal ingress is not itself an egress firewall.

Replace sample image references with tested images from your registry, preferably pinned by
digest. The root README records package/image release availability; examples do not establish that
a particular public tag has shipped. The 2 CPU / 4 GiB resource limits are starting configurations,
not a throughput guarantee. Load-test actual reports and monitor queue wait, failures, CPU, and memory.

For Kubernetes, install the seccomp profile on every node that can run the pod, at
`/var/lib/kubelet/seccomp/atli-reports/chromium.json` (the manifest's header explains why it is a
`Localhost` profile and what it means for Ubuntu 23.10+ nodes). Generate credentials, then create
`reports-auth` in your selected namespace from the server file. Remove diagnostics permission for an
ordinary application key first:

```bash
kubectl --namespace <namespace> create secret generic reports-auth \
  --from-env-file=.reports-secrets/server.env
kubectl --namespace <namespace> apply -f deploy/kubernetes/reports.yaml
```

The Kubernetes example uses API keys and self-contained assets so it needs no outbound connections.
JWT metadata and OTLP collectors require deliberately scoped egress rules. Enabling JWT while
denying identity-provider discovery causes authentication to fail closed; it does not disable auth.
Current older Aspire Kubernetes publishers need a probe workaround; the native manifest includes
the actual probes rather than relying on Aspire's local readiness check.

## Managed hosting boundary

The [isolated renderer experiment](isolated-workers.md) documents the optional worker boundary,
its launcher authority, performance comparisons, and the gates for a future hosted service.

The self-hosted implementation supplies portable caller identity, permissions, local admission,
and rendering policy. It deliberately has no accounts database, token issuer, billing system, or
shared cloud worker pool. A managed service must add those components before accepting hostile
customer documents; adding an API key to the current shared browser is insufficient.

Keep a public API outside renderer workers. It resolves authenticated tenant membership, applies
deployment-wide quotas, and passes a bounded document job to a worker without reusable customer
credentials. Run workers in a separately validated OS sandbox or stronger isolation boundary with
restricted egress, limited storage, and no access to the control plane's secrets. Browser contexts
remain useful storage separation within the supported trust domain but are not that boundary.
For durable jobs, add a durable queue and private PDF storage with tenant authorization, expiry,
and ownership checks on every retrieval. Do not derive tenancy from a caller-provided header.

Audit identity, policy outcome, status, and duration; never record document content, credentials,
or sensitive asset URLs. A hosted release additionally needs adversarial containment and
cross-tenant tests against its actual runtime, fair global admission, isolation-aware scheduling,
rotation/revocation across replicas, and incident-response procedures.

## Validation

The test suites cover API-key verification/rotation/expiry, real JWT validation and permission
checks, forged identity claims, admission/cancellation/deadlines, client credential transport,
network policy/address validation, asset limits and redirects, browser environment reduction, and
real Chromium network sentinels. `.github/scripts/smoke-test-server-image.sh` checks the actual
NativeAOT image with authentication, a read-only filesystem, minimal capabilities, anonymous
probe privacy, and a browser environment canary. Under `deploy/seccomp/chromium.json` it checks that
no browser process runs with `--no-sandbox` and that renderers run outside the browser's user namespace;
under Docker's default profile, that the server fails closed with the remedy in its health details
and log. `chromium-seccomp-profile.py check` verifies the profile against its pinned upstream.
`smoke-test-server-jwt.py` validates real HTTPS
discovery, RSA signatures, token lifetime, audience/issuer, and permissions in the NativeAOT image.
`validate-kubernetes-security.sh` creates and removes a disposable kind cluster to exercise
authenticated conversions, Chromium's sandbox under the `Localhost` profile, probes, blocked
external assets, and rolling restarts. The default
kind CNI does not establish NetworkPolicy enforcement, and the HPA is validated as a resource
without a load/scaling test. These checks do not prove hostile-browser containment or validate a
customer's cloud network policy. The Azure template is compile-validated; it has not been deployed
to a live Azure subscription by this test suite.

References: [ASP.NET JWT authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication),
[ACA service authorization](https://learn.microsoft.com/en-us/azure/container-apps/authentication-entra),
[Kubernetes service accounts](https://kubernetes.io/docs/concepts/security/service-accounts/),
[OWASP SSRF prevention](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html).
