# Hosted renderers in a production shape on Azure Container Apps Sandboxes — amd64, 2026-10-04

This run deploys the [hosted renderer service](../../docs/hosted-renderers.md) the way production
would run it, in one region, and measures it there. The renderers run in a sandbox group connected
to a virtual network that has no usable DNS; the server in
[gateway mode](../../docs/engine/server.md#gateway-mode) runs as an Azure Container App behind a NAT
gateway with a fixed address, reads renderer records from Key Vault, and wakes `Manual` renderers
through a managed identity that holds only a resume-only role; the
[provisioner](../../src/Atli.Reports.Provisioner/README.md) creates the renderers with ports that
admit only the NAT gateway's address and the operator's. A client sandbox in the same region sends
the conversions. It checks the renderers' hardening with the server image, and measures creating
renderers with and without the virtual network, waking them through the gateway, warm latency and
the gateway's overhead, 15 minutes of mixed load with one and two gateway replicas, and a rollout
under load.

Raw values: [`2026-10-04-6cdce25-hosted-renderers-production-amd64.json`](2026-10-04-6cdce25-hosted-renderers-production-amd64.json).
Reproduce with [`benchmarks/azure-sandboxes/production.py`](../azure-sandboxes/production.py) (one
subcommand per phase; it runs [`gateway-client.py`](../azure-sandboxes/gateway-client.py) in the
client sandbox and the probes beside it in renderers), which creates resource groups of its own and
deletes them with `cleanup`.

## Results

| Measurement | Connected group (virtual network, DNS denied) | Control group (no network) |
|---|---|---|
| Hardening with the server image: no name resolves (also straight to `168.63.129.16`), egress denied, metadata blocked, Chromium's sandbox, conversions, the port through the proxy | All pass | DNS resolves, as expected; the rest pass |
| The client's request to a renderer's port | `403 IpAccessDenied`; the stopped renderer stayed stopped | |
| Create call, median (range), 6 each | 2.02 s (0.95 to 3.03) | 0.81 s (0.77 to 0.93) |
| `/health/ready` after the create call started, median (range) | 2.44 s (1.38 to 3.48) | 1.24 s (1.20 to 1.34) |
| OnDemand wake through the gateway, invoice | `vm1`: median 1.62 s (1.49 to 4.65), 5; `vm2`: 1.55 to 2.07 s, 5 | `cm1`: median 0.71 s (0.65 to 0.82), 5; again 0.76 to 0.99 s, 5 |
| OnDemand wake through the gateway, 49-page report | Median 4.01 s (3.75 to 6.61), 5 | Median 3.18 s (2.98 to 3.44), 5 |
| Wake after auto-suspend (S), invoice | 1.66 and 1.21 s | |
| `Manual` port woken by the gateway's resume (resume-only role), invoice | 2.80 s with a cold token, then 2.09 and 0.76 s; resume calls 1.38, 0.43, 0.50 s | |
| Warm invoice through the gateway, p50 (25 each) | S 0.077, M 0.088, L 0.059 s | M 0.077 s |
| Warm 49-page report through the gateway, p50 | S 2.97, M 2.36, L 2.18 s | M 2.34 s |
| The gateway's overhead (same renderer, direct against through the gateway) | | Invoice +5 ms (p50), report +0.10 s (p50) |

| Load through the gateway, six connected tenants (2 S, 2 M, 2 L) | Result |
|---|---|
| 15 min at the admitted concurrency, 1 replica | 35,062 requests, all `200`; 38.96 PDFs/s; gateway 0.27 cores, 252 MiB |
| 15 min at the admitted concurrency, 2 replicas | 35,060 requests, all `200`; 38.96 PDFs/s; 0.18 and 0.15 cores |
| 5 min at twice the admitted concurrency, 1 replica | 29 % `503 Busy` from the gateway at once; no renderer `429`; 38.93 PDFs/s |
| 5 min at twice the admitted concurrency, 2 replicas | 17 % `503 Busy` (388 after 8 resends); 8,031 renderer `429`s resent; 37.93 PDFs/s; longer tails |
| Renderer VM memory peaks; OOM kills | S 971 of 1,213 MiB at most, M 1,043 of 2,218, L 1,458 of 4,163; none |
| Rollout of six renderers under load, 2 replicas | 165.6 s (the 150 s drain included); 8,727 of 8,727 requests during it `200` |

Against the earlier runs: the control group's OnDemand invoice wakes through the gateway (0.65 to
0.99 s) match the follow-up's 0.63 to 1.50 s measured straight to the port; new renderers were ready
sooner than the first run's 1.4 to 5.2 s, the connected ones included, rather than the 33 s the
follow-up saw for its one connected sandbox; the warm 49-page report took 2.2 to 3.0 s through the
gateway against about 2.1 s inside the VM; and throughput stayed at about one conversion per vCPU.

## Conditions and limits

- Region `eastus2`, 2026-10-04 from 12:57 to 16:01 UTC, the measurements from 13:15 to 15:20.
  `aca` CLI `1.0.0-preview.4`, Azure CLI 2.87.0, Sandboxes data-plane API `2026-02-01-preview`,
  Container Apps ARM API `2024-03-01`.
- Server sources at `6cdce25`. The renderers' disk images (`aca sandboxgroup disk create --source`)
  and the gateway's image (`az acr build`) were both built from a `git archive` of that commit's
  server build context and Dockerfile (Chrome for Testing 154.0.8037.92). The harness was
  committed after the run and changed between phases where a phase failed or needed more (the
  JSON's `notes` list each change). Each recorded phase ran as committed, except that the first wake
  phase predates the resume before each stop and the `--wake-pair` option, which it did not need.
- Everything ran in `rg-atli-reports-prod-shape-20261004125734`, tagged
  `purpose=atli-reports-production-shape`, and the Container Apps environment's infrastructure
  group `rg-atli-reports-prod-shape-20261004125734-cae`, which the platform created under the name
  the environment asked for (and the run tagged); plus one custom role definition assignable only in
  the first group.
- **Renderer network:** `vnet-renderers`, 10.42.0.0/16, DNS server `10.42.0.4` (an address of
  its range that no subnet holds); subnet `renderers`, 10.42.1.0/24, delegated to
  `Microsoft.App/environments`, with a network security group whose one custom rule denies all
  outbound traffic to the `AzurePlatformDNS` service tag. `aca sandboxgroup network create --name
  renderers` connected the renderer sandbox group to it (5.9 s).
- **Gateway network:** a second virtual network, 10.60.0.0/16 with Azure's default DNS; subnet
  `gateway`, 10.60.0.0/27, delegated to `Microsoft.App/environments`, with a NAT gateway and a
  static Standard public IPv4 address. A workload-profiles Container Apps environment (Consumption
  profile only, external) on that subnet took 11 min 19 s to create.
- **Sandbox groups:** renderers connected to the network ("connected" below), control renderers
  without a network ("control"), and the client. Disk images: a and b (the rollout target) in the
  connected group, a in the control group, built in parallel in 149, 153, and 164 s.
- **Record store:** a Key Vault with RBAC authorization, 7-day soft-delete retention, and no purge
  protection. The operator's login had Key Vault Secrets Officer on it.
- **Gateway identity:** one user-assigned managed identity with AcrPull on the registry, Key Vault
  Secrets User on the vault, and a custom role on the connected sandbox group with no `Actions` and
  the `DataActions` `Microsoft.App/sandboxGroups/sandboxes/read` and
  `Microsoft.App/sandboxGroups/sandboxes/resume/action`. No Data Owner role.
- **Gateway:** Container App `ca-gateway`, 1 vCPU and 2 GiB per replica, image pulled from a Basic
  registry with the identity, external HTTPS ingress to port 8080, liveness probe on
  `/health/live` and readiness probe on `/health/ready`. `ReportsServer__Mode=Gateway`, API-key
  authentication with one caller (`reports.convert` only; the key from
  `scripts/create-reports-api-key.sh`, its ID and verifier as Container App secrets) that belongs
  to all nine tenants and names one in `X-Reports-Tenant`, `MaxConcurrentRequestsPerCaller=64` (so
  the tenant limits, not the caller's default of 4, are what the load meets),
  `Records:Store=KeyVault` and `Wake:Mode=Sandboxes` (the connected group) with the identity's
  client ID. `Wake:Timeout`, `RendererTimeout`, the record cache (30 s), and
  `MaxConcurrentRequestsPerTenant` (8) kept their defaults. Each phase deployed it with the logging
  it needed (request logs for the wakes, event 56 at `Debug` for the load), which made a new
  revision; a new revision answered `/health/ready` 31 to 42 s after its deployment started.
- **Provisioner:** a Release build of `6cdce25` on the laptop (macOS, arm64) with the Azure CLI
  login, `Records:Store=KeyVault`, `NetworkConnection=renderers` for the connected group (empty for
  the control group), `PortActivation=OnDemand` (`Manual` for `vman`), `AutoSuspendAfter` 5 min
  (60 s for `vauto`), and `AllowedSourceCidrs` the NAT gateway's `/32` and the laptop's `/32`; for
  `cm1` also the client sandbox's ten listed outbound addresses, so the client could reach it
  directly in section 4.
- **Tenants:** connected `vs1`, `vs2` (S: 0.5 vCPU, 1 GiB, one conversion, admits 2), `vm1`,
  `vm2` (M: 1 vCPU, 2 GiB, one conversion, admits 2), `vl1`, `vl2` (L: 2 vCPU, 4 GiB, two
  conversions, admits 4), `vman` (M, `Manual` port), `vauto` (S, auto-suspend after 60 s); control
  `cm1` (M).
- **Client:** one sandbox in its own group without a network, `python-3.12` disk, 4 vCPU and
  8 GiB, egress denied except to the gateway's host and `*.adcproxy.io`, auto-suspend off. Its
  requests leave through the platform's egress proxy, so every client-side time includes that
  proxy. The client's CPU stayed low in every phase (load average under 0.1 at the end of each run;
  25 to 66 CPU-seconds per load run).
- Fixtures as in the earlier Sandboxes runs: the invoice (69,600-byte PDF) and the 49-page report
  (7,186,708 bytes), A4, 0.4 in margins, backgrounds, tagged PDFs. Request bodies are 9,056 bytes
  (invoice) and about 460 KB (report).
- Timings come from the client (`time.monotonic()` around each request), the gateway's log (its
  own timestamps), the data plane from the laptop (create, stop, and state), and Azure Monitor
  (the gateway's CPU and memory, one-minute means). The client's and the gateway's clocks were not
  compared.
- One region, one morning, small samples per cell; the sample size is stated with each number.

## 1. Hardening in the connected group, with the server image

Inside `vm1` (connected, M), with `aca sandbox exec` as the server's user (1654), running
[`dns-probe.pl`](../azure-sandboxes/dns-probe.pl),
[`network-probe.sh`](../azure-sandboxes/network-probe.sh) (which now also requests
`http://1.1.1.1/` by address), and
[`chromium-sandbox-check.sh`](../azure-sandboxes/chromium-sandbox-check.sh); `cm1` (control, M)
for comparison:

| Check | `vm1`, connected | `cm1`, control |
|---|---|---|
| `/etc/resolv.conf` | `nameserver 10.42.0.4` (the network's DNS setting) | `nameserver 168.63.129.16` |
| A and TXT for `example.com`, A for a random name under it, TXT for `microsoft.com`, through the configured resolver | Timeout, each | Answered (`NOERROR`, 2, 2, 0, and 15 records) |
| A for `example.com` sent straight to `168.63.129.16` | Timeout | Answered |
| The same to `8.8.8.8` and `1.1.1.1` | Timeout | Timeout |
| `getent hosts example.com` | Fails | Resolves |
| HTTP `GET http://example.com/` | No answer (the name does not resolve) | `403` from the platform's egress proxy |
| HTTP `GET http://1.1.1.1/` by address | `403` from the egress proxy | `403` from the egress proxy |
| TCP to `1.1.1.1:443`; to `8.8.8.8:53` | Accepted by the egress proxy; blocked | The same |
| TCP to `169.254.169.254:80` (instance metadata) | Blocked | Blocked |
| Managed-identity endpoint | `401 unauthorized_client` (the group has no identity) | The same |
| `chromium-sandbox-check.sh` | Passes: no `--no-sandbox`, 3 processes outside the browser's user namespace | Passes, 3 |
| VM | Kernel `6.12.8+`, 1 vCPU, 2,271,600 kB | The same |
| Invoice and 49-page report through the gateway, from the client | `200` PDFs in 0.19 and 2.18 s | `200` PDFs in 0.09 and 2.50 s |
| `/health/ready` through the port proxy, from the laptop (an allowed address) | `200` in 0.18 s | `200` in 0.16 s |

So the server image runs unchanged in a group whose network has no DNS: no name resolves, including
a query sent straight to Azure DNS, which the follow-up run had left open for a plain sandbox. HTTP
egress still meets the platform's egress proxy, which refuses it, and the browser starts with its
sandbox. The probes in `vm1` took 75 s, every lookup waiting for its timeout.

**The port refuses the client, and the refusal wakes nothing.** `vm1` was stopped (stop call
8.05 s). From the client sandbox, whose addresses `vm1`'s port does not admit, `POST /convert` and
`GET /health/live` to `vm1`'s port URL both answered
`403 {"error":"Access denied by IP access control policy","errorCode":"IpAccessDenied"}`, in 0.036
and 0.025 s. `vm1` was still `Stopped` 10 s later. The next invoice through the gateway woke it and
returned its PDF in 1.66 s: **the gateway, a Container App on a subnet with a NAT gateway, presents
the NAT gateway's address to the port proxy**, which the allow-list admitted. Every conversion of
this run through the gateway confirms the same; none was refused by address.

**No browser in the gateway.** `az containerapp exec` into the gateway's replica listed three
processes: PID 1 `/usr/bin/tini -- ./Atli.Reports.Server`, the server (PID 7), and the exec's own
`ls`; a fourth exec for more detail was refused with `429` (retry after 600 s). The gateway's log
held no line from the engine or a browser.

The first two attempts at these probes returned nothing from `vm1`. Run in one `aca sandbox exec`,
the lookups outlasted the request, which ends after about a minute; run in the background, the
probes could not write their output, since the files `aca sandbox fs write` creates belong to root
and commands run as the server's user. The recorded probes ran in the background and wrote to
`/tmp`.

## 2. Creating renderers: connected against control

Six rounds, each creating one size-M renderer in each group, alternating which went first, over the
data plane from the laptop with one cached token, with the body the provisioner sends
(the server image's entrypoint, deny-by-default egress, the renderer environment with a throwaway
credential, auto-suspend, and `customerVnetConnectionName: "renderers"` in the connected group);
then the port (`OnDemand`, the two allowed `/32`s); then `/health/ready` from the laptop every
0.1 s; then a delete.

| Seconds | Connected (6) | Control (6) |
|---|---|---|
| Create call (returns `Running`) | 0.95, 1.15, 1.18, 2.85, 2.97, 3.03 (median 2.02) | 0.77, 0.79, 0.80, 0.81, 0.82, 0.93 (median 0.81) |
| Port call | 0.18 to 0.22 | 0.19 to 0.21 |
| `/health/ready` `200` after the create call started | 1.38, 1.58, 1.60, 3.28, 3.41, 3.48 (median 2.44) | 1.20, 1.23, 1.23, 1.24, 1.25, 1.34 (median 1.24) |
| Delete call | 8.41 to 9.49 | 5.98 to 7.09 |

- Every renderer answered `200` on the first readiness probe: the create call returns once the
  server is up.
- **The connected group adds 0.2 to 2.2 s per create, not the 33 s of the follow-up's one
  `python-3.12` sandbox.** Its times fell in two clusters, about 1.1 s and about 3 s, half each.
- The provisioner's own `create` of the nine tenants (`tenants` in the JSON): in the connected
  group, create calls of 1.19 to 6.20 s (the first, `vs1`, the slowest) and ready 1.6 to 6.9 s
  after the call started; for `cm1` in the control group, 1.10 and 1.6 s. Its readiness polling
  runs every 0.5 s.
- The rollout in section 6 created six connected renderers, four at once, with create calls of 0.7
  to 3.3 s, each ready 1.2 to 3.7 s after its create call started.

## 3. Waking through the gateway

Seconds from the client sending the request to the complete PDF, through the gateway, one request
on a new connection. Each OnDemand cycle stopped `vm1` (connected) and `cm1` (control), both M,
through the data plane at the same time (memory mode), waited 5 s, and sent the waking request to
one and then the other, alternating the order; the cycles alternate the invoice and the 49-page
report as the waking request. Each cycle came more than 30 s after the previous one, so every wake
also read the record from Key Vault again.

| Waking request | Connected `vm1` (5 each) | Control `cm1` (5 each) |
|---|---|---|
| Invoice | 1.49, 1.58, 1.62, 3.26, 4.65 (median 1.62) | 0.65, 0.69, 0.71, 0.73, 0.82 (median 0.71) |
| 49-page report | 3.75, 3.80, 4.01, 4.45, 6.61 (median 4.01) | 2.98, 3.07, 3.18, 3.31, 3.44 (median 3.18) |
| Stop call until `Stopped` | 7.4 to 9.8 | 4.6 to 8.3 |
| Next invoice, warm | 0.09 to 0.15 | 0.08 to 0.14 |

- Every waking request returned its PDF; nothing failed or was resent by the client.
- **Waking a connected renderer took about 0.9 s longer at the median**, with outliers to 3 s
  more. A second pair, run later the same way with invoices only, agreed: `vm2` (connected, M)
  1.55, 1.70, 1.81, 2.05, 2.07 s (median 1.81) against `cm1` 0.76, 0.83, 0.84, 0.96, 0.99 s
  (median 0.84). Two connected renderers and one control renderer were measured, so a property of
  `cm1` alone is not ruled out.
- The control renderer matched the follow-up's OnDemand invoice wakes (0.63 to 1.50 s, measured
  straight to the port), although these went through the gateway and a Key Vault read.
- **Auto-suspend:** `vauto` (connected, S, 60 s) was observed `Stopped` (`stoppedReason: Idle`)
  96.8 and 141.7 s after its last request (polled every 2 s); the waking invoice through the gateway
  took 1.66 and 1.21 s, the next one 0.09 s.

**`Manual` port, woken by the gateway's resume.** `vman` (connected, M, `Manual` port). The gateway
was restarted first (13.0 s until it answered ready again), so the first cycle met cold token
caches for the data plane; its readiness probe had already used the Key Vault token. Each cycle
stopped `vman`, waited 5 s, sent an invoice through the gateway, and waited 35 s more. From the
gateway's log (request logging on):

| Cycle | Client, to the PDF | Gateway, request start to event 60 | Resume call (event 61) | Event 61 to the response's end | Gateway's request duration |
|---|---:|---:|---:|---:|---:|
| 1 (after the restart) | 2.80 | 0.717 | 1.376 | 0.677 | 2.769 |
| 2 | 2.09 | 0.326 | 0.425 | 1.321 | 2.071 |
| 3 | 0.76 | 0.088 | 0.499 | 0.157 | 0.743 |

```text
13:46:16.643 info: Microsoft.AspNetCore.Hosting.Diagnostics[1] Request starting HTTP/1.1 POST http://<gateway host>/convert - application/json 9056
13:46:17.360 info: Atli.Reports.Server.Gateway.SandboxWaker[60] Resuming sandbox b36fba5d-37c0-4b56-a3ff-2d99295da6fe, the renderer of tenant vman.
13:46:18.736 info: Atli.Reports.Server.Gateway.SandboxWaker[61] Sandbox b36fba5d-37c0-4b56-a3ff-2d99295da6fe, the renderer of tenant vman, resumed (Running) in 1376.2477 ms.
13:46:19.413 info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 POST http://<gateway host>/convert - 200 - application/pdf 2769.3850ms
```

- **The resume-only role works for the gateway's identity in Azure.** Every resume succeeded with
  only `sandboxes/read` and `sandboxes/resume/action`; no event 62 or 63 (a failed resume or state
  read) appeared.
- Request start to event 60 covers the record read from Key Vault, the first forward and its
  `403 {"error":"Sandbox is not running"}`, the data-plane token, and the state read. It took
  0.72 s with a cold data-plane token and 0.09 to 0.33 s after, so **the identity's first
  data-plane token cost about 0.4 to 0.6 s**. The gateway does not log the token separately; this
  is the difference between cycles.
- The resume call itself took 0.43 to 1.38 s (the e2e run: 0.43 s). After it, the resent invoice
  arrived in 0.16 s once; in the other cycles it took 0.68 and 1.32 s, which suggests the gateway
  met a not-running answer or a refused connection right after the resume and resent after its
  250 ms back-off (the log, at `Information`, does not show resends).
- Against OnDemand wakes of the same size in the same group (median 1.62 s), the `Manual` path with
  warm tokens was 0.76 to 2.09 s.

## 4. Warm latency and the gateway's overhead

Sequential conversions from the client on one kept-alive connection per target, the targets
interleaved round by round: the first request of each (a new connection, and a wake if the renderer
had suspended) is left out, then 25 timed rounds. `cm1_direct` is the same control renderer's port
URL straight from the client, with the renderer's own credential (copied into the client for this
step and deleted after): `cm1`'s port admits the client's ten outbound addresses for that reason
only; no connected renderer admits them.

| Target, 25 samples each | Invoice p50 | p95 | max | 49-page report p50 | p95 | max |
|---|---:|---:|---:|---:|---:|---:|
| `vs1` (connected, S) via the gateway | 0.077 | 0.089 | 0.108 | 2.969 | 3.114 | 3.144 |
| `vm1` (connected, M) via the gateway | 0.088 | 0.091 | 0.094 | 2.356 | 2.474 | 2.557 |
| `vl1` (connected, L) via the gateway | 0.059 | 0.073 | 0.074 | 2.177 | 2.288 | 2.343 |
| `cm1` (control, M) via the gateway | 0.077 | 0.089 | 0.111 | 2.344 | 2.551 | 2.681 |
| `cm1` straight to its port | 0.072 | 0.080 | 0.080 | 2.248 | 2.302 | 2.337 |

- **The gateway adds about 5 ms to an invoice (p50; 9 ms at p95) and about 0.1 s to the 49-page
  report (p50; 0.25 s at p95)**, from the same client to the same renderer. That covers the
  Container Apps ingress, the gateway, its egress through the NAT gateway, and relaying 7.2 MB once
  more; both paths include the port proxy and the client's egress proxy.
- The 49-page report took 2.2 to 3.0 s warm, as in the earlier runs measured inside the VM (about
  2.1 s at 1 and 2 vCPU, 2.8 s at 0.5 vCPU): S is slower, M and L alike for one report at a time.

## 5. Sustained load through the gateway

All six load tenants at once, from the client, through the gateway. Each tenant had as many
workers as its renderer admits (2 for S and M, 4 for L; 16 in all), each on a kept-alive
connection, sending invoices back to back and every 20th request the 49-page report (5 % of
requests, about 85 % of the bytes). A `429` or `503` with `Retry-After` made that worker wait as
long (always 1 s), as a well-behaved caller would. A sampler in each renderer recorded the VM's peak
memory (`MemTotal - MemAvailable`, every 0.25 s), its CPU busy share, and OOM kills (`/proc/vmstat`)
over the run plus about two minutes. The gateway logged event 56 (each resend to a busy renderer)
at `Debug`, and its CPU and memory come from the app's Azure Monitor metrics, one-minute means per
replica, over the minutes wholly inside each run.

Four runs: the admitted concurrency for 15 minutes with one gateway replica (r1) and with two
(r2, minimum and maximum 2), then twice the admitted concurrency for 5 minutes with one (o1) and
two (o2), since at the admitted concurrency the caller itself never sends a tenant more than its
renderer admits, whatever the replica count.

| Run | Replicas | Workers | Requests | PDFs/s | Non-`200` answers | Renderer `429`s the gateway resent (event 56) | Gateway CPU, cores: mean (max minute) | Gateway working set, MiB: mean (max) | OOM kills |
|---|---:|---|---:|---:|---|---:|---|---|---:|
| r1, 15 min | 1 | Admitted, 16 | 35,062 | 38.96 | None | 0 | 0.27 (0.30) | 252 (277) | 0 |
| r2, 15 min | 2 | Admitted, 16 | 35,060 | 38.96 | None | 0 | 0.18 and 0.15 (0.23, 0.18) | 241 and 231 (270, 261) | 0 |
| o1, 5 min | 1 | Twice, 32 | 16,466 | 38.93 | 4,784 `503 Busy` from the tenant limit; 3 connection errors | 0 | 0.24 (0.28) | 282 (289) | 0 |
| o2, 5 min | 2 | Twice, 32 | 13,759 | 37.93 | 2,379 `503 Busy`: 1,991 from the tenant limits, 388 after 8 resends to a busy renderer; 2 connection errors | 8,031 | 0.15 and 0.14 (0.20, 0.18) | 240 and 257 (259, 272) | 0 |

Per tenant in r1, seconds (r2 differed by at most 0.13 invoices/s and 0.01 reports/s per tenant;
its latencies are in the JSON):

| Tenant (size, workers) | Invoices/s | Invoice p50 / p95 / p99 | Reports/s | Report p50 / p95 / p99 | VM peak, MiB of total | VM CPU busy |
|---|---:|---|---:|---|---|---:|
| `vs1` (S, 2) | 4.49 | 0.130 / 2.93 / 3.13 | 0.24 | 3.02 / 3.20 / 3.25 | 794 of 1,213 | 85 % |
| `vs2` (S, 2) | 4.29 | 0.142 / 3.01 / 3.27 | 0.22 | 3.10 / 3.36 / 3.47 | 875 of 1,213 | 85 % |
| `vm1` (M, 2) | 4.97 | 0.148 / 2.38 / 2.51 | 0.26 | 2.43 / 2.66 / 2.79 | 1,014 of 2,218 | 85 % |
| `vm2` (M, 2) | 5.00 | 0.148 / 0.28 / 2.48 | 0.26 | 2.43 / 4.75 / 5.00 | 974 of 2,218 | 86 % |
| `vl1` (L, 4) | 9.04 | 0.173 / 0.85 / 2.62 | 0.48 | 2.69 / 4.89 / 5.39 | 1,437 of 4,163 | 85 % |
| `vl2` (L, 4) | 9.22 | 0.174 / 1.00 / 2.51 | 0.48 | 2.82 / 4.79 / 5.75 | 1,458 of 4,163 | 85 % |

The VM totals are those of the [first Sandboxes run](2026-10-03-5b667b4-azure-sandboxes-amd64.md)
for each size (`vm1` reported 2,271,600 kB here, the same 2,218 MiB).

- **Throughput is the renderers', not the gateway's.** About 39 PDFs/s (16.5 MB/s relayed) in
  every run, the same with one replica or two and with twice the callers. The renderers' VMs were
  81 to 86 % busy; the gateway used about 0.27 of its one vCPU.
- **At the admitted concurrency nothing failed, with one replica or two:** 70,122 requests, all
  `200` PDFs; no `503 Busy` from the gateway and no renderer `429`. The race between a finished
  request releasing its tenant slot and the caller's next request did not show.
- **Two replicas split the work evenly** (17,530 conversions each, by the gateway's event 20) and
  between them used 0.32 cores against 0.27 for one; neither replica's tenant limit filled, since
  the caller kept only the admitted number in flight.
- **S and M performed alike for this mix.** With one conversion at a time, the report dominates
  each renderer's time (at M, 0.26 reports/s at 2.4 s each is about 0.63 of every second), and the
  report took 3.0 s at S against 2.4 s at M. An invoice that arrives behind a report waits for it,
  so the invoice's p95 at S and M was 2.4 to 3.0 s against a p50 of 0.13 to 0.15 s; L, with two
  conversions, kept it to 0.85 to 1.7 s.
- **Memory stayed within every size**, with no OOM kill in any run: S peaked at 794 to 971 MiB of
  about 1,213 (80 % at most, the closest to its limit), M at 928 to 1,043 of 2,218, L at 1,397 to
  1,458 of 4,163.
- **One replica holds each tenant to what its renderer admits.** At twice the concurrency (o1) the
  gateway refused the excess at once with `503 Busy` and `Retry-After: 1` (a median of 3 ms at the
  client) and logged event 53 for each, 4,784 times in 5 minutes. No renderer answered `429`, and
  throughput and latencies matched r1.
- **Two replicas send a renderer up to twice what it admits.** In o2 each replica admitted up to
  the tenant's limit, so the renderers refused the excess with their own `429`: the gateway resent
  8,031 times in 5 minutes (27 per second across six tenants), and 388 conversions still met `429`
  after the eighth resend and reached the caller as `503 Busy`, after 1.8 to 3.3 s (62 such answers
  among the first 400 recorded). The other 1,991 `503 Busy` were each replica's own limit.
  Throughput fell 2.6 %, the report's p95 rose to 2.9 to 5.9 s and its p99 to 4.8 to 8.0 s, and
  invoices' worst cases to 4.1 to 5.9 s. The renderers did not take more work than they admit
  (their per-caller limit and queue refused it), so their memory and CPU stayed as in o1 and no OOM
  kill occurred.
- Five requests (3 in o1, 2 in o2) failed at the client with a connection that the other end had
  closed (`SSLEOFError`, `RemoteDisconnected`, within 0.3 ms of sending), only in the runs with
  `503`s. Which hop closed them was not established.

## 6. Rollout under load

Two gateway replicas, the admitted concurrency, the same mix, for 6 minutes. `cm1`, `vman`, and
`vauto` were deleted first: the provisioner treats every record in its store as its group's (see
[What else the run found](#what-else-the-run-found)). 60.0 s into the load,
`rollout --disk-image <disk b> --max-parallel 4` started, with the default drain of 2 min 30 s.

| Seconds after the rollout started | Rollout output |
|---:|---|
| 3.72 | 6 to replace, 0 already on the image, 4 at a time |
| 3.78 to 3.93 | Replacing `vl2`, `vm2`, `vl1`, `vm1` |
| 6.70 to 7.15 | Their replacements created (create calls of 2.9 to 3.3 s) |
| 7.15 to 7.57 | Ready 3.3 to 3.7 s after their create calls |
| 7.32 to 7.74 | Their records point to the new sandboxes |
| 7.37 to 10.78 | `vs1` and `vs2` the same way (create calls of 0.7 and 2.8 s, ready 1.2 and 3.2 s after them) |
| 161.04 to 164.89 | The six old sandboxes deleted after the drain |
| 165.58 | No leftover renderer sandboxes; replaced 6, failed 0 |

- **Every request during the rollout succeeded:** from 10 s before it to 40 s after it
  (load seconds 50 to 270), 8,727 of 8,727 requests returned a PDF. The gateway kept each cached
  record up to 30 s after its move and then used the new renderer.
- Over the 6 minutes, 14,281 of 14,282 requests succeeded (39.7 PDFs/s). The one failure came 56 s
  before the rollout: `vl2`'s renderer answered `408` after about 10 s, which the gateway logged
  (event 45) and relayed as `504 Timeout`. The server answers `408` when a request body arrives too
  slowly (Kestrel's minimum data rate), so the gateway's upload to the renderer stalled; it does
  not resend such an answer.
- `vs1`'s reports were slower over this run (p50 3.67 s, p95 7.30 s, against 3.02 and 3.20 s in
  r1); the run does not tell the old renderer's minute from the new one's. The other tenants
  matched r1.
- `prune --drain 00:00:00` found nothing, and `list` showed the six tenants `Running` on disk b.
- The rollout's duration, 165.6 s, is its drain plus about 15 s of work; the replacements took
  11 s for six renderers in the connected group.

## Bugs found

None in the gateway, the provisioner, or the hosting library: every behaviour this run exercised
matched [gateway mode](../../docs/engine/server.md#gateway-mode) and the
[provisioner's README](../../src/Atli.Reports.Provisioner/README.md). The harness had three, fixed
in `production.py` before the recorded runs: an `az acr build --file` path the CLI resolved against
the working directory, probes that outlived the `aca sandbox exec` request and then could not write
their output (section 1), and a wake cycle that tried to stop a renderer that had already
auto-suspended (`409`).

## What else the run found

- **The gateway's address is the NAT gateway's.** A Container App on a subnet with a NAT gateway
  reaches the port proxy from the NAT gateway's public address, so a port allow-list of that `/32`
  (and the operator's) admits the gateway and refuses everyone else, the client sandbox in the same
  region included.
- **A record store serves one sandbox group.** The provisioner reads every record in its store as
  one of its group's renderers: `list` in the connected group showed `cm1`, a control-group tenant
  in the same vault, as `missing`, and a `rollout` without `--tenant` would have replaced it with a
  renderer in the connected group. The gateway's `Wake:Sandboxes` names one group too. This run
  shared one vault between two groups only for the comparison and deleted `cm1` before the rollout.
- **One caller with several tenants needs a higher caller limit.** The gateway's caller admission
  applies before the tenant limits, with a default of 4 requests in flight per caller; this run set
  64 so that six tenants' 16 to 32 requests could be in flight.
- **Refusals are logged one line each.** At twice the admitted concurrency one replica logged event
  53 about 16 times a second and every conversion logs event 20 (35,062 lines in r1); 131,144
  gateway lines in all, none from a browser or the engine.
- Each gateway start logs two Data Protection warnings (keys kept in the container and not
  encrypted). The gateway uses no Data Protection feature in this configuration.
- Each wake after more than 30 s idle also read the tenant's record from Key Vault, which the wake
  times above include.

## What this does not show

- More than one renderer per group in the wake comparison (two connected, one control), or another
  region, day, or time of day; samples are 3 to 25 per cell.
- Why connected renderers wake more slowly, or why their creates fall into two clusters.
- Private ingress to renderers or to the gateway: the renderers' ports and the gateway's ingress
  are public endpoints, gated by addresses and API keys.
- JWT callers, several callers, per-caller quotas, or a real customer application.
- Load beyond 15 minutes, bursts, other documents, documents with assets, a gateway near its CPU
  limit, scale rules for the gateway (only fixed replica counts), or Key Vault throttling at more
  tenants.
- What happens to a renderer driven past its admission limit by many replicas: its own admission
  held at two replicas and twice the callers.
- Rollouts of more than six tenants, with longer conversions in flight, or with a drain shorter
  than the record cache; a rollout of the control group.
- The platform's egress decision logs, and DNS security policies or private resolvers in place of
  the network security group rule.

## Cleanup

- `production.py cleanup` (15:26 to 16:01 UTC) had the provisioner delete the six remaining tenants
  (`delete --tenant`, 1.8 to 4.0 s each, the record first and then the sandbox); `cm1`, `vman`,
  and `vauto` had been deleted before the rollout the same way. It then deleted the gateway
  identity's three role assignments and the operator's Key Vault role assignment, the custom role
  definition, the Log Analytics workspace (with `--force`, so it is not kept soft-deleted), the
  Container App and its environment (which removed the infrastructure group), the client and
  control sandbox groups (17 and 16 s), the connected group's network connection and the group
  itself (the CLI stopped waiting after 616 s and exited 1; deleting the resource group finished
  it), and the resource group, gone 22 minutes after its delete started. The Key Vault, which went
  to soft-delete with its group, was purged.
- Checked afterwards, at 16:03 UTC: `az group exists` answers `false` for both groups, and no
  `rg-atli-reports-prod-shape-*` group remains; no resource carries the
  `purpose=atli-reports-production-shape` tag; no role assignment's scope names those groups, and
  none belongs to the gateway identity; the custom role definition is gone, by ID and by name; the
  identity is gone; the vault is neither active nor soft-deleted; the workspace is not among the
  deleted workspaces.
- Every provisioner log, the ACR build log, every phase's log, the raw results, and the committed
  files were searched for the caller's key, its ID and verifier, the 15 renderer credentials of the
  run, bearer tokens, API-key-shaped strings, the subscription and tenant IDs, the operator's and
  the identity's object and client IDs, the workspace's ID, the gateway's host name, this machine's
  public address, the NAT gateway's address, the client's ten outbound addresses, and any other
  public IPv4 address: none was found. The work directory (keys, logs, state), the scratch files,
  and the temporary `aca` CLI were removed afterwards.
