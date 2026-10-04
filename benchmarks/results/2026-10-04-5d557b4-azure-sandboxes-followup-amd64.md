# Azure Container Apps Sandboxes follow-up — amd64, 2026-10-04

This run answers questions the [first Sandboxes run](2026-10-03-5b667b4-azure-sandboxes-amd64.md)
left open for the [hosted renderer design](../../docs/hosted-renderers.md#azure-container-apps-sandboxes):
whether a renderer port can admit a service principal by Entra ID, whether a principal limited to
reading and resuming sandboxes can do anything else, whether the DNS channel out of a renderer can
be closed, how one renderer behaves under concurrent load, and whether a stopped renderer can be
woken by the request itself.

Raw values: [`2026-10-04-5d557b4-azure-sandboxes-followup-amd64.json`](2026-10-04-5d557b4-azure-sandboxes-followup-amd64.json).
Reproduce with [`benchmarks/azure-sandboxes/followup.py`](../azure-sandboxes/followup.py) (one
subcommand per experiment; it runs [`role-probe.py`](../azure-sandboxes/role-probe.py),
[`dns-probe.pl`](../azure-sandboxes/dns-probe.pl), [`load.py`](../azure-sandboxes/load.py), and
[`wake.py`](../azure-sandboxes/wake.py) inside sandboxes).

## Conditions and limits

- Region `eastus2`, 2026-10-04 between 01:52 and 03:04 UTC. `aca` CLI `1.0.0-preview.4` (the
  latest from `aka.ms/aca-cli-install` that day, the same version as the first run); data-plane API
  `2026-02-01-preview`. The data plane also advertised `2026-09-01-preview`, and ARM lists
  `2026-07-01` for `Microsoft.App/sandboxGroups`; neither was used.
- The disk image was built by the platform from the server Dockerfile at `5d557b4` in 125.5 s. The
  new scripts were not yet committed; the server sources are those of `5d557b4`. The resource
  group, the two sandbox groups, and the disk were created by a script equivalent to
  `followup.py setup` before that subcommand existed; the other subcommands produced the recorded
  values, except where a section says otherwise.
- Renderers ran the image's entrypoint with deny-by-default egress, no auto-suspend unless a
  section says otherwise, and the environment of
  [`RendererServerEnvironment`](../../src/Atli.Reports.Hosting/Renderers/RendererServerEnvironment.cs):
  API-key authentication with a key generated for the run, caller `gateway`, `reports.convert`
  only, document networking disabled. Section 4 states the concurrency settings of each renderer.
- The Data Owner role that `aca sandboxgroup create` grants its caller worked on the data plane
  4.3 and 6.5 s after the two groups were created (805 s in the first run's run 3).
- Fixtures and conversion options as in the first run: A4, 0.4 in margins, backgrounds, tagged
  PDFs. The 49-page report's PDF is 7.19 MB.
- Timings come from three places: inside a sandbox (`role-probe.py`, `load.py`, `wake.py`, all
  timed with `time.monotonic()`), from client sandboxes in the region reaching renderers through the
  platform's port proxy, and from the laptop for ingress probes only. The laptop's round trip to the
  port proxy was about 0.15 to 0.2 s, and it was under unrelated load (load average about 80).
- One evening, one region, small samples; nothing here measures behaviour over days or quotas.

## 1. Ingress authentication

The CLI's `aca sandbox port add` offers only `--anonymous` or `--email`, and `aca sandbox schema`
lists only `auth.anonymous` for ports. The data plane has more, as the CLI binary's model names and
the Python SDK (`azure-containerapps-sandbox` 0.1.0b4, 2026-07-17) show. On `2026-02-01-preview`,
`POST .../sandboxes/{id}/ports/add` accepted `auth.entraId.objectIds`, `ipAccessControl`, and
`activationMode`, and the port views it returned carry these fields (the other `entraId` lists and
`Http2` were not tried):

| Port field | Values |
|---|---|
| `auth.anonymous` | `true` or `false` |
| `auth.entraId` | `enabled`, `emails`, `emailSuffixes`, `objectIds`, `tenantIds` |
| `ipAccessControl` | `defaultAction` (`Allow` or `Deny`) and up to 10 `rules`: `name`, `action`, `priority` (0 to 1000, lowest first), `sourceCidrs` (1 to 10) |
| `activationMode` | `Manual` (the default) or `OnDemand` |
| `protocol` | `Http` or `Http2` |

The bulk `PUT .../ports` of `2026-02-01-preview` wants whole port views (it rejected a body
without `url`), so `followup.py` changes a port by removing and re-adding it.

**An Entra ID-gated port admits browsers, not services.** With `auth.entraId.objectIds` set to the
signed-in user's object ID:

| Request | Result |
|---|---|
| No credentials | `401 {"error":"Unauthorized","message":"Authentication required"}`, `WWW-Authenticate: Bearer realm="eastus2.adcproxy.io"` |
| `Accept: text/html` | `302` to `auth.eastus2.adcproxy.io/auth/entra`, then `302` to `login.microsoftonline.com/common/oauth2/v2.0/authorize`: authorization code flow, client `409cf302-c83f-43c3-94eb-ca581ab18c6d` (the platform's own app), scope `openid profile email`, cookie `adc_entra_state` |
| User bearer token, audience `https://dynamicsessions.io`, `https://management.azuredevcompute.io`, `https://management.azure.com/`, or `https://graph.microsoft.com` | `401` |
| User bearer token for resource `409cf302-…` (Entra issued an encrypted JWE) | `401` |
| Audience `api://409cf302-…` or `https://eastus2.adcproxy.io` | No token issued to the Azure CLI |
| Managed identity allowed by `objectIds`, tokens for `https://dynamicsessions.io`, `https://management.azure.com/`, or `409cf302-…` (section 2's tester) | `401` each |
| Managed identity token for the port URL as the resource | Not issued: `AADSTS500011` |

The sign-in ends in a consent page and session cookies set by the platform
([microsoft/azure-container-apps#1840](https://github.com/microsoft/azure-container-apps/issues/1840)
describes the flow). No bearer-token audience that the proxy accepts was found, so in this preview
neither a service principal nor a managed identity, the gateway's natural identities, can pass an
Entra-gated port non-interactively. The per-renderer API key stays the gate.

**The source-IP allow-list works.** With `ipAccessControl` denying by default and allowing one
`/32`, requests from that address reached the server (`200`); with only `203.0.113.0/24` allowed,
the same requests got `403 {"error":"Access denied by IP access control policy","errorCode":"IpAccessDenied"}`
from the proxy. Which source address a request from a Container Apps app or from another sandbox
presents was not tested; a sandbox's `GET` view lists ten `outboundIpAddresses`, which look shared,
so allowing a sandbox's egress addresses would not identify one caller.

**`OnDemand` activation resumes a stopped sandbox on request.** With `activationMode: OnDemand`, a
request to a stopped sandbox's port was held, the sandbox resumed, and the request was answered:
`200` 0.48, 0.54, and 0.56 s after it was sent from the laptop, against a small Python server.
Section 5 measures it with the renderer. Requests the port refused did not wake it: after an
IP-denied request (`403`) and after an unauthenticated request to an Entra-gated port (`401`), the
sandbox was still `Stopped` 10 s later.

Private ingress was not tested. The same issue names a private hostname,
`<sandbox-id>--<port>.<environment-id>.<region>.azurecontainerapps.io`, for sandbox groups linked to
an Express-mode Container Apps environment (`aca sandboxgroup create --environment-id`, a link
that cannot be changed or removed).

## 2. A resume-only custom role

A custom role with no control-plane actions and two data actions,
`Microsoft.App/sandboxGroups/sandboxes/read` and
`Microsoft.App/sandboxGroups/sandboxes/resume/action`, assignable only in the run's resource
group, was assigned on the renderer sandbox group to a user-assigned managed identity. A second
sandbox group (the tester) got that identity as its group identity
(`aca sandboxgroup identity assign --user-assigned`). A tester sandbox, with egress denied except
to `management.eastus2.azuredevcompute.io` and `*.adcproxy.io`, took a token for
`https://dynamicsessions.io` from its `IDENTITY_ENDPOINT` (`api-version=2019-08-01`,
`client_id` of the identity, header `X-IDENTITY-HEADER`) and called the renderer group's data
plane against a running S renderer. No Entra app registration was needed.

| Call (`.../sandboxGroups/{group}/...`, `api-version=2026-02-01-preview`) | Status |
|---|---|
| `GET sandboxes/{id}` | 200 |
| `GET sandboxes` | 200 |
| `POST sandboxes/{id}/resume` (renderer stopped by the owner first) | 200 in 0.64 s; `Running` 0.52 s later |
| `GET sandboxes/{id}/ports` | 403 |
| `POST sandboxes/{id}/stop` | 403 |
| `POST sandboxes/{id}/executeShellCommand` | 403 |
| `POST sandboxes/{id}/executeCommand` | 403 |
| `GET sandboxes/{id}/files?path=/etc/hostname`, `GET sandboxes/{id}/files/list` | 403 |
| `PUT sandboxes/{id}/files?path=/tmp/role-probe` | 403 |
| `POST sandboxes/{id}/egresspolicy` (default `Allow`) | 403 |
| `POST sandboxes/{id}/lifecycle` | 403 |
| `POST sandboxes/{id}/ports/add` (anonymous port 9999) | 403 |
| `POST sandboxes/{id}/snapshot`, `.../commit`, `.../disable` | 403 |
| `GET sandboxes/{id}/stats`, `GET sandboxes/{id}/egress-decisions` | 403 |
| `PUT sandboxes` (create) | 403 |
| `DELETE sandboxes/{id}` | 403; the renderer was still `Running` |
| `GET secrets`, `GET diskimages`, `GET snapshots` | 403 |

- The `403` bodies were empty.
- The first call, 7.5 minutes after the role assignment was created, already succeeded; how much
  sooner it would have was not measured.
- `GET sandboxes/{id}` with `sandboxes/read` returns the sandbox's ports (their URLs and settings),
  egress policy, labels, lifecycle, resources, outbound addresses, and state, even though
  `GET .../ports` needs `ports/read`. It does not return the environment, so the API key's verifier
  and ID stay out of it.
- The role assignment, the role definition, and the identity were deleted afterwards (see
  [Cleanup](#cleanup)).

## 3. DNS from a renderer

The egress policy has host rules (`Allow`, `Deny`, `Transform`, `Rewrite` on host, path, and
method) and a `trafficInspection` mode, and nothing for DNS. Sandbox groups can be connected to a
virtual network (`aca sandboxgroup network create --vnet-subnet-id`, sandbox field
`customerVnetConnectionName`). [`dns-probe.pl`](../azure-sandboxes/dns-probe.pl) sends raw UDP
queries from inside a sandbox.

Without a virtual network, from an S renderer with egress denied by default:

| Query | Result |
|---|---|
| Resolver in `/etc/resolv.conf` | `168.63.129.16` (Azure DNS) |
| `example.com` A, `example.com` TXT | Answered (2 records each) |
| `microsoft.com` TXT | Answered (15 records) |
| A random 12-letter name under `example.com` | Answered (`NOERROR`, no records); no cache can hold a fresh random name, so the query reached the zone's servers |
| UDP 53 to `8.8.8.8` or `1.1.1.1` | Timed out |

Egress rules did not change these results: neither `Deny` rules for `*.example.com`,
`example.com`, and `*.microsoft.com` nor an `Allow` rule for `example.com`. Arbitrary names and TXT
records resolve, so a compromised renderer can tunnel data out and back through DNS.

**A virtual network moves DNS where it can be closed.** A virtual network with its DNS server set
to an unused address (`10.42.0.4`) and a `/24` subnet delegated to `Microsoft.App/environments` was
connected to the tester group (about 10 s), and a sandbox created with that connection (33 s,
against about 1 s without it):

| Step | Result in the connected sandbox |
|---|---|
| `/etc/resolv.conf` | `nameserver 10.42.0.4`: the virtual network's DNS setting |
| Queries to `10.42.0.4` | Timed out |
| A query sent straight to `168.63.129.16` | Still answered |
| NSG on the subnet denying outbound to `168.63.129.16/32` port 53 (after about 50 s) | Still answered |
| NSG rule changed to deny the `AzurePlatformDNS` service tag (after about 2 min) | Timed out: no DNS at all |
| HTTP to `1.1.1.1` by address, IMDS | `403` from the platform's egress proxy; IMDS blocked |
| Port 8080 through the public port proxy | `200` |

So a connected sandbox group's DNS goes through the customer's virtual network, where a resolver
policy or the `AzurePlatformDNS` deny rule applies, while HTTP egress still passes the platform's
egress proxy and ports still work. The server image was not run in a connected group, and Azure DNS
security policies or a private resolver were not tried.

The virtual-network steps were run by hand, not by `followup.py`: `az network vnet create
--address-prefixes 10.42.0.0/16 --dns-servers 10.42.0.4 --subnet-prefixes 10.42.0.0/24`,
`az network vnet subnet update --delegations Microsoft.App/environments`,
`aca sandboxgroup network create --vnet-subnet-id <subnet> --name dnsprobe`, a `PUT sandboxes` with
`"customerVnetConnectionName": "dnsprobe"` and the `python-3.12` disk, `dns-probe.pl` in it, and an
NSG on the subnet with one outbound `Deny` rule, first to `168.63.129.16/32` port 53 and then to
the `AzurePlatformDNS` tag on all ports.

## 4. Concurrent load on one renderer

[`load.py`](../azure-sandboxes/load.py) ran in a client sandbox in the region (`python-3.12` disk,
2 vCPU, 4 GiB, egress allowed only to `*.adcproxy.io`) against one renderer's port URL. Each of N
workers keeps one HTTPS connection through the platform's port proxy, sends one warmup request, and
then sends the same request back to back for 20 s (invoice) or 45 s (49-page report); requests
started before the deadline finish. PDFs per second count complete `%PDF-` responses over the
time to the last one; latencies are of those PDFs only. A sampler in the renderer VM recorded the
VM's peak memory (`MemTotal - MemAvailable`), its CPU busy share, and OOM kills
(`/proc/vmstat`) over the cell plus 5 s, so the CPU share understates load slightly.

Three server configurations per size:

- **Sweep:** `MaxConcurrentConversions` 8 and per-caller limit 16, so that N clients mean N
  conversions in the browser at once.
- **Planned:** as `RendererServerEnvironment` sets them from
  [`RendererSize`](../../src/Atli.Reports.Hosting/Renderers/RendererSize.cs): conversions and the
  per-caller limit both 4 (L), 2 (M), or 1 (S). Excess requests get `429` at once.
- **One per vCPU:** conversions 2 (L) or 1 (M and S), per-caller limit twice that, so one request
  per slot waits in the engine's queue instead of being refused.

Above the per-caller limit, the client waited for `Retry-After` (always `1`) after each `429`, as
a gateway would. Every `429` was the server's own
`{"title":"Conversion capacity is exhausted.", "kind":"Busy"}`, never the proxy's.

**L, 2 vCPU, 4 GiB**

| Configuration | Fixture | Clients | PDFs/s | p50 s | p95 s | Statuses | VM peak MiB |
|---|---|---:|---:|---:|---:|---|---:|
| Sweep | invoice | 1 | 20.5 | 0.048 | 0.056 | 410 × 200 | 410 |
| Sweep | invoice | 2 | 23.6 | 0.084 | 0.107 | 474 × 200 | 459 |
| Sweep | invoice | 4 | 24.5 | 0.160 | 0.219 | 492 × 200 | 474 |
| Sweep | invoice | 8 | 24.8 | 0.320 | 0.396 | 502 × 200 | 527 |
| Sweep | 49-page report | 1 | 0.47 | 2.08 | 2.23 | 22 × 200 | 916 |
| Sweep | 49-page report | 2 | 0.85 | 2.34 | 2.51 | 40 × 200 | 1401 |
| Sweep | 49-page report | 4 | 0.78 | 5.05 | 5.79 | 36 × 200 | 2349 |
| Sweep | 49-page report | 8 | 0.64 | 12.14 | 13.18 | 32 × 200 | 4119 of 4163 |
| Planned (4, 4) | invoice | 4 | 25.5 | 0.154 | 0.209 | 512 × 200 | 468 |
| Planned (4, 4) | invoice | 8 | 26.4 | 0.152 | 0.200 | 531 × 200, 80 × 429 | 451 |
| Planned (4, 4) | 49-page report | 4 | 0.77 | 5.04 | 6.22 | 36 × 200 | 2332 |
| Planned (4, 4) | 49-page report | 8 | 0.81 | 4.82 | 6.41 | 39 × 200, 180 × 429 | 2313 |
| One per vCPU (2, 4) | invoice | 4 | 26.1 | 0.152 | 0.178 | 526 × 200 | 442 |
| One per vCPU (2, 4) | invoice | 8 | 26.0 | 0.151 | 0.179 | 524 × 200, 80 × 429 | 450 |
| One per vCPU (2, 4) | 49-page report | 4 | 0.81 | 4.92 | 5.20 | 40 × 200 | 1396 |
| One per vCPU (2, 4) | 49-page report | 8 | 0.81 | 4.91 | 5.22 | 40 × 200, 180 × 429 | 1411 |

**M, 1 vCPU, 2 GiB**

| Configuration | Fixture | Clients | PDFs/s | p50 s | p95 s | Statuses | VM peak MiB |
|---|---|---:|---:|---:|---:|---|---:|
| Sweep | invoice | 1 | 14.4 | 0.068 | 0.078 | 288 × 200 | 292 |
| Sweep | invoice | 2 | 13.3 | 0.145 | 0.188 | 267 × 200 | 327 |
| Sweep | invoice | 4 | 13.1 | 0.303 | 0.376 | 264 × 200 | 381 |
| Sweep | invoice | 8 | 12.6 | 0.639 | 0.755 | 256 × 200 | 471 |
| Sweep | 49-page report | 1 | 0.45 | 2.22 | 2.32 | 21 × 200 | 854 |
| Sweep | 49-page report | 2 | 0.39 | 4.99 | 5.42 | 18 × 200 | 1358 |
| Sweep | 49-page report | 4 | 0.35 | 11.21 | 12.46 | 16 × 200 | 2184 |
| Sweep | 49-page report | 8 | 0.03 | 36.94 | 37.02 | 2 × 200, 8 × 500, 6 × 504 | 2218; 5 OOM kills |
| Planned (2, 2) | invoice | 2 | 14.2 | 0.139 | 0.170 | 284 × 200 | 308 |
| Planned (2, 2) | invoice | 8 | 14.4 | 0.137 | 0.172 | 289 × 200, 120 × 429 | 328 |
| Planned (2, 2) | 49-page report | 2 | 0.40 | 5.05 | 5.39 | 18 × 200 | 1267 |
| Planned (2, 2) | 49-page report | 8 | 0.41 | 4.80 | 5.09 | 20 × 200, 270 × 429 | 1302 |
| One per vCPU (1, 2) | invoice | 2 | 15.2 | 0.129 | 0.146 | 305 × 200 | 289 |
| One per vCPU (1, 2) | invoice | 8 | 15.2 | 0.130 | 0.142 | 305 × 200, 120 × 429 | 312 |
| One per vCPU (1, 2) | 49-page report | 2 | 0.46 | 4.33 | 4.63 | 22 × 200 | 815 |
| One per vCPU (1, 2) | 49-page report | 8 | 0.45 | 4.41 | 4.68 | 22 × 200, 270 × 429 | 842 |

**S, 0.5 vCPU, 1 GiB** (concurrency 1 and 2 only)

| Configuration | Fixture | Clients | PDFs/s | p50 s | p95 s | Statuses | VM peak MiB |
|---|---|---:|---:|---:|---:|---|---:|
| Sweep | invoice | 1 | 14.5 | 0.066 | 0.084 | 291 × 200 | 282 |
| Sweep | invoice | 2 | 14.1 | 0.139 | 0.182 | 283 × 200 | 319 |
| Sweep | 49-page report | 1 | 0.34 | 2.89 | 3.19 | 16 × 200 | 708 |
| Sweep | 49-page report | 2 | 0.32 | 6.15 | 6.99 | 16 × 200 | 1102 |
| Planned (1, 1) | invoice | 1 | 13.7 | 0.071 | 0.082 | 275 × 200 | 284 |
| Planned (1, 1) | invoice | 8 | 13.9 | 0.071 | 0.079 | 279 × 200, 140 × 429 | 304 |
| Planned (1, 1) | 49-page report | 1 | 0.34 | 2.95 | 3.15 | 16 × 200 | 708 |
| Planned (1, 1) | 49-page report | 8 | 0.32 | 3.12 | 3.25 | 15 × 200, 308 × 429 | 738 |
| One per vCPU (1, 2) | invoice | 2 | 14.8 | 0.132 | 0.157 | 297 × 200 | 281 |
| One per vCPU (1, 2) | invoice | 8 | 15.0 | 0.132 | 0.146 | 301 × 200, 120 × 429 | 309 |
| One per vCPU (1, 2) | 49-page report | 2 | 0.34 | 5.83 | 6.11 | 17 × 200 | 716 |
| One per vCPU (1, 2) | 49-page report | 8 | 0.35 | 5.74 | 6.09 | 17 × 200, 270 × 429 | 735 |

No recorded cell had an OOM kill except the M sweep at eight concurrent reports. Every recorded
cell ran its full duration (`elapsed_s` in the JSON), and every cell measured with the final
`load.py` reports all its workers finished (`workers_finished`).

- **Throughput stops at one conversion per vCPU.** At L, a second concurrent conversion raised the
  report's throughput from 0.47 to 0.85 PDFs/s and the invoice's from 20.5 to 23.6; more only
  lengthened latency. At M and S, nothing beyond one conversion helped: two or more lowered
  throughput slightly and doubled latency for each step.
- **Memory grows by about 400 to 500 MiB per concurrent 49-page report.** Before the first
  conversion the VM used 230 to 330 MiB. At M, four concurrent reports peaked at 2184 MiB of a
  2048 MiB VM (2 OOM kills in the first,
  discarded attempt at that cell; none in the recorded one), and eight caused 5 OOM kills,
  `500 The document could not be converted.` and `504 The conversion did not finish in time.`,
  and `/health/ready` answered `503` afterwards. At L, eight concurrent reports peaked at 4119 of
  4163 MiB and all succeeded, slowly.
- **The planned limits are safe for this report but not the best choice.** Planned L (4) and M (2)
  had no errors at their own limit and refused the excess at once with `429`, but at the cost of
  memory: 2332 MiB at L and 1267 MiB at M, against 1396 and 815 MiB for one conversion per vCPU
  with one queued request per slot, which matched or beat them on throughput (L: 26.1 against
  25.5 invoices/s, 0.81 against 0.77 reports/s; M: 15.2 against 14.2 invoices/s, 0.46 against
  0.40 reports/s) and on report latency.
- A new renderer of each configuration answered `/health/ready` 0.21 to 0.36 s after the create
  call returned, measured from the laptop.
- The first attempt at some cells failed and was rerun; `load_discarded_first_attempt` in the JSON
  keeps those cells. Three long cells ran in one `aca sandbox exec` request, which the CLI abandons
  after about a minute, so they returned nothing; the final `followup.py` starts the client and
  the sampler in the background and polls. In two planned M invoice cells the first `load.py`'s
  workers stopped after their first `429` (cause not established), so every cell with `429`s was
  rerun with a `load.py` that records exceptions and per-worker completion. One of those discarded
  cells, at concurrency 2 against the planned M limit of 2, had 2 `429`s in 163 requests, which
  the rerun (284 requests) did not repeat.

## 5. Waking a stopped renderer

An M renderer (1 vCPU, 2 GiB; two conversions at once, per-caller limit raised to 16 so that a
burst measures the platform rather than the server's `429`) with port 8080 set to
`{"port":8080,"auth":{"anonymous":true},"activationMode":"OnDemand"}`. The field sits on the port,
as `GET sandboxes/{id}` shows it. Each cycle stopped the renderer through the data plane (memory
mode; `Stopped` 10.5 to 17.1 s after the call), waited 5 s, and then sent requests from a client
sandbox in the region ([`wake.py`](../azure-sandboxes/wake.py), a new connection per request).
Seconds from sending to the complete PDF:

| Wake | Cycle 1 | Cycle 2 | Cycle 3 |
|---|---:|---:|---:|
| OnDemand, one invoice | 0.89 | 1.50 | 0.63 |
| OnDemand, one 49-page report (7.19 MB) | 2.84 | 4.04 | |
| OnDemand, 4 invoices and 2 reports sent at once: invoices | 2.35, 2.22, 2.33, 2.45 | 4.17, 3.99, 4.08, 3.94 | |
| the same burst: reports | 7.22, 7.23 | 10.68, 10.81 | |
| Manual, then an explicit resume, one invoice: first request | `403 {"error":"Sandbox is not running"}` after 0.05 | the same | the same |
| resume call | 0.72 | 1.46 | 0.76 |
| first request to the complete PDF | 1.86 | 2.90 | 1.56 |

- Every request sent to the stopped renderer under OnDemand succeeded with a complete PDF, the
  7.19 MB report and every request of the burst included. The proxy held the requests while the
  renderer resumed; none got `403`, `502`, or `503`. The burst's invoices queued behind the
  renderer's two conversion slots, which explains their times.
- The browser was still warm: the next invoice after each wake took 0.09 to 0.20 s, a new TLS
  connection included. Warm, before any stop, the invoice took 0.41 s on its first connection
  and the report 2.26 s.
- Under Manual activation, the retry after the resume call succeeded on its first attempt in each
  cycle.
- **Auto-suspend still works after OnDemand wakes.** With auto-suspend at 60 s idle (memory mode),
  the renderer was observed `Stopped` (`stoppedReason: Idle`) 126.5 and 141.9 s after its last
  request, polled every 2 s, and the next request woke it again: the invoice arrived after 3.25
  and 0.99 s.
- **Disable is a kill switch that OnDemand respects.** On another M renderer with an OnDemand port
  (planned limits, section 4), three wakes took 3.55, 0.87, and 0.87 s for an invoice through
  `curl`. Then `POST sandboxes/{id}/disable` stopped it (`stoppedReason: Disabled`); a request got
  `403` in 0.04 s and the renderer stayed stopped, and an explicit resume got
  `409 SandboxAdminDisabled`. `POST .../enable` returned `200`.
- Requests the port itself refuses do not wake it (section 1). With an anonymous port, any request
  to the URL wakes the renderer; the server checks the API key only after the wake.

## What this suggests for the design

These are inputs for the design and the gateway and provisioner implementations, not changes made
here.

- **Gateway to renderer: keep the per-renderer API key.** Entra-gated ports admit interactive
  sign-ins only. Where the gateway's egress address is fixed, the port's IP allow-list is a second
  gate, and it also keeps strangers from waking a renderer; what address a Container Apps app
  presents was not tested.
- **Waking: `activationMode: OnDemand` on the renderer port removes the need for any wake
  permission on the request path.** The request itself resumes the renderer, faster than `403`,
  a resume call, and a retry (an invoice in 0.63 to 1.50 s against 1.56 to 2.90 s, in the region),
  with large bodies and concurrent requests intact, and auto-suspend keeps working. With an
  anonymous OnDemand port anyone holding the URL can wake (and bill) the renderer; the API key is
  checked only after the wake. If an explicit resume is kept as a fallback, the resume-only role
  works as intended: `sandboxes/read` and `sandboxes/resume/action` allow reading and resuming and
  nothing else tested, and reading exposes port URLs, egress policy, labels, and state but not the
  environment.
- **Compromise response:** `POST sandboxes/{id}/disable` (Data Owner, the provisioner) stops a
  renderer and blocks both OnDemand wakes and explicit resumes until `enable`.
- **DNS:** connecting renderer sandbox groups to a virtual network whose DNS is unavailable or
  filtered, with an NSG rule denying `AzurePlatformDNS`, closes the DNS channel; renderers need no
  DNS (documents are self-contained, document networking disabled). Without a virtual network,
  DNS stays open. Running the server image in such a group is the next check.
- **Concurrency defaults:** `MaxConcurrentConversions` S 1, M 1, L 2 (one per vCPU, at least one),
  and the renderer's `MaxConcurrentRequestsPerCaller` S 2, M 2, L 4, so one request per slot waits
  in the engine's queue. The gateway's per-tenant in-flight limit should equal the renderer's
  per-caller limit: 2 for the default size M (S 2, L 4). Above it the gateway queues or rejects
  itself; a renderer `429` (`kind: Busy`, `Retry-After: 1`) means the gateway exceeded the
  limit and should wait for `Retry-After` before retrying. At S, the second queued request adds
  latency (the report's p50 5.8 s against 2.9 s) for 8 % more invoices per second; a limit of 1
  there is equally defensible.
- **Facts the implementations need:**
  - Data plane `https://management.{region}.azuredevcompute.io`, token audience
    `https://dynamicsessions.io` (scope `https://dynamicsessions.io/.default`), `api-version`
    `2026-02-01-preview`.
  - A sandbox's managed identity: `GET $IDENTITY_ENDPOINT?api-version=2019-08-01&resource=...&client_id=...`
    with header `X-IDENTITY-HEADER: $IDENTITY_HEADER`.
  - A port: `POST sandboxes/{id}/ports/add` with `{"port":8080,"auth":{"anonymous":true},"activationMode":"OnDemand"}`,
    optionally `"ipAccessControl":{"defaultAction":"Deny","rules":[{"name":...,"action":"Allow","priority":10,"sourceCidrs":[...]}]}`.
    Changing a port means `ports/remove` then `ports/add` on `2026-02-01-preview`.
  - The resume-only role: `"DataActions": ["Microsoft.App/sandboxGroups/sandboxes/read", "Microsoft.App/sandboxGroups/sandboxes/resume/action"]`,
    no `Actions`, assigned on the renderer sandbox group's resource ID.
  - `aca 1.0.0-preview.4`'s `sandboxgroup identity remove --user-assigned` fails with `400`; an ARM
    `PATCH` of the sandbox group with `{"identity":{"type":"None"}}` clears it.
- **Disk builds:** at `5d557b4` the server project references `src/Atli.Reports.Client` and
  `src/Atli.Reports.Hosting`, which the server Dockerfile does not copy. The platform built that
  commit's Dockerfile anyway (the server code uses nothing from those projects yet, so the missing
  references only warn), and every renderer here ran it. A build will fail once the server uses
  them, until the Dockerfile copies them.

## What this does not show

- Entra-authenticated access to a port by a service principal through some other mechanism than a
  bearer token, or private ingress through an Express-mode environment.
- Which source address a Container Apps app presents to a port's IP allow-list.
- The server image in a VNet-connected sandbox group, DNS security policies, or a private resolver.
- Load from more than one client, sustained load beyond a minute per cell, other fixtures, or
  more than one renderer at a time.
- Other regions, quotas, or behaviour over days.

## Cleanup

Everything ran in one resource group, `rg-atli-reports-followup-20261004015238` (tagged
`purpose=atli-reports-sandboxes-followup`), except the custom role definition, which lives at the
subscription with the resource group as its only assignable scope.

- The role assignment, the role definition, and the user-assigned identity were deleted after
  section 2, and afterwards no role definition with that name, no assignment for the identity,
  and no such identity remained. The tester group's reference to the identity was cleared with an
  ARM `PATCH`; the CLI's `identity remove` failed (see above).
- The tester sandbox group was deleted in 31 s and the renderer sandbox group in about 10 minutes
  (with their sandboxes, disk image, and VNet connection). No role assignment for the signed-in
  user remained on either group.
- The resource group, which then held only the virtual network and the NSG, was deleted:
  `az group exists` answered `false` at 03:16 UTC, and no `rg-atli-reports-followup-*` group
  remained in the subscription.
