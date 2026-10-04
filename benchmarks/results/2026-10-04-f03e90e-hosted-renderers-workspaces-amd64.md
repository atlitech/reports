# Applications with many tenants on Azure Container Apps Sandboxes — amd64, 2026-10-04

This run validates [applications with many tenants](../../docs/hosted-renderers.md#applications-with-many-tenants)
on Azure, in the production shape of the
[earlier run](2026-10-04-6cdce25-hosted-renderers-production-amd64.md): renderers in a virtual
network without DNS, records in Key Vault, and the server in
[gateway mode](../../docs/engine/server.md#gateway-mode) as a Container App behind a NAT gateway.
New this time is the [provisioning service](../../src/Atli.Reports.Provisioner/README.md#the-provisioning-service)
(`serve`), a second Container App on internal ingress that creates a renderer on a workspace's first
conversion. Two applications, `app-a` (prefix `appa-`, at most 60 renderers, 20 creates a minute)
and `app-b` (prefix `appb-`, at most 5, 5 a minute), name their workspaces' tenants in the tenant
header. A client sandbox in the region sends their requests. The run checks isolation between the
two, then measures a burst of 40 new workspaces, 10 simultaneous first requests to one, the quota,
fairness while one application floods the gateway with new tenant IDs, 10 minutes of steady use,
deletes, the kill switch, retirement of idle renderers, a rollout that retires stopped renderers,
and the cleanup of a leftover sandbox.

It found three bugs, each fixed and re-checked during the run (see [Bugs found](#bugs-found)). The
most serious: the provisioner listed only the first 25 sandboxes of a group, so in a group with
more renderers the kill switch could report success without disabling anything, and an
application's delete could remove a workspace's record but leave its renderer.

Raw values: [`2026-10-04-f03e90e-hosted-renderers-workspaces-amd64.json`](2026-10-04-f03e90e-hosted-renderers-workspaces-amd64.json).
Reproduce with [`benchmarks/azure-sandboxes/workspaces.py`](../azure-sandboxes/workspaces.py) (one
subcommand per scenario; it runs [`workspace-client.py`](../azure-sandboxes/workspace-client.py) in
the client sandbox), which creates a resource group of its own and deletes it with `cleanup`.

## Results

| Scenario | Result |
|---|---|
| 1. Isolation | Every cross-application, out-of-prefix, prefix-alone, and reserved name refused with `403` (13 checks; an invalid ID's delete and a missing header `400`), and nothing created. The service's internal name resolves publicly, but every request to it from the internet got the platform's `404` or `421` |
| 2. 40 new workspaces at once | 40 of 40 got a PDF; the first 20 in 2.2 to 4.7 s, the other 20 in 62.2 to 66.6 s (prefix's 20 creates a minute); 19 `503 Busy` from the gateway's budget of new tenants (`Retry-After: 1`) and 20 from the service's rate limit (`Retry-After: 60`); exactly 40 sandboxes and 40 records, no duplicate or leftover |
| 3. 10 first requests to one workspace at once | One sandbox, one ensure call from the gateway, one creation; 10 of 10 PDFs, 8 in 1.8 to 2.5 s, 2 after the gateway's per-tenant limit refused them (3.2 and 3.3 s, honouring `Retry-After: 1`) |
| 4. Quota (app-b, 5) | 5 created in 1.6 to 3.6 s; the sixth `503 BrowserUnavailable` with the quota message (also a minute later); exactly 5 renderers |
| 5. Fairness: app-a floods new IDs for 70 s | 282,841 requests (4,041/s): 281,118 refused by app-a's budget in 6 ms (p50), 1,198 by the service's rate limit, 198 by the quota once app-a reached 60, 21 created. app-b's existing workspace 40 of 40 PDFs; its two new ones created in 1.7 and 1.8 s. app-a's recently used workspaces 48 of 48; its workspaces idle for 28 minutes waited up to 72.7 s for a lookup. Key Vault: no `429`, at most 15 not-found reads a second. The gateway's vCPU saturated (0.996 cores, one-minute mean) and it logged 250 MB |
| 6. Steady use, 10 min, 30 workspaces | 3,609 of 3,609 PDFs. Warm invoice p50 / p95 / p99 0.086 / 0.129 / 0.170 s; warm 49-page report 2.90 / 3.16 / 3.30 s; first request, created 1.91 / 2.08 / 2.08 s (10); first request, woken 1.56 / 4.23 / 8.24 s (20); after 180 s idle 1.25 / 1.62 s (7). Gateway 0.036 cores, 196 MiB; service 0.003 cores, 207 MiB |
| 7. Delete (5) | Five `204` in 0.44 to 0.81 s; records and sandboxes gone; the next conversion recreated one in 3.93 s |
| 8. Kill switch | `disable` (15.8 s, the renderer was running); the app's `DELETE` `409`, nothing deleted; conversions `503 BrowserUnavailable` in 28 to 72 ms; after `enable`, a PDF in 1.54 s |
| 9. Retirement (5 min idle, checked every minute) | The first run retired 43 idle renderers in 20 s; three watched workspaces retired 5 min 22 s to 5 min 51 s after they stopped, then recreated by their next conversion in 4.05 to 5.80 s; a workspace converting every 20 s kept its renderer (75 of 75 PDFs) |
| 10. Rollout `--stopped retire` under light load | 4 running renderers replaced on disk b (ready 3.0 to 3.7 s after their create calls) and 5 stopped ones retired, none failed; 1,800 of 1,800 light-load conversions returned PDFs, 374 during the job; a retired workspace came back on disk b in 6.46 s |
| 11. Leftover cleanup | Found 6.5 min and deleted 9.0 min after it was created (5.5 min of protection, the next run, and the 2.5 min drain) |

## Conditions and limits

- Region `eastus2`, 2026-10-04 from 20:02 to 23:11 UTC, the scenarios from 20:42 to
  22:33. `aca` CLI `1.0.0-preview.4`, Azure CLI 2.87.0, Sandboxes data-plane API
  `2026-02-01-preview` (and `2026-09-01-preview` for listings after the fix), Container Apps ARM
  API `2024-03-01`.
- **Sources.** The branch head was `f03e90e`. The gateway's image (`az acr build`) and the
  renderers' disk images a and b (`aca sandboxgroup disk create --source`) were built from a
  `git archive` of `f03e90e`'s server build context and Dockerfile (Chrome for Testing
  154.0.8037.92). The provisioning service ran `f03e90e` until 21:04:58, `1d108c9` (the listing
  fix) until 22:15:20, and `5b23334` (the quota fix) after; before 21:04:58 ran scenarios 1 and 2
  and the first leftover's creation. The operator's provisioner CLI on the laptop ran `f03e90e` for
  the reproduction of the listing bug and `1d108c9` or later after it. The server's code is the
  same in all three commits except for the hosting library's sandbox listing, which the gateway
  does not call. The harness was committed after the run, and changed between phases where a phase
  failed or needed more (the JSON's `notes` and `annotate`d keys say where).
- Everything ran in `rg-atli-reports-workspaces-20261004200214`, tagged
  `purpose=atli-reports-workspaces`, and the Container Apps environment's infrastructure group
  `rg-atli-reports-workspaces-20261004200214-cae`, which the platform created under the name the
  environment asked for (and the run tagged); plus one custom role definition assignable only in the
  first group.
- **Renderer network:** `vnet-renderers`, 10.42.0.0/16, DNS server `10.42.0.4` (an address no subnet
  holds); subnet `renderers`, 10.42.1.0/24, delegated to `Microsoft.App/environments`, with a network
  security group whose one custom rule denies outbound traffic to the `AzurePlatformDNS` service
  tag. The renderer sandbox group is connected to it as `renderers`.
- **Gateway network:** a second virtual network, 10.60.0.0/16 with Azure's DNS; subnet `gateway`,
  10.60.0.0/27, with a NAT gateway and a static Standard public IPv4 address, and on it a
  workload-profiles Container Apps environment (Consumption only, external), which took 17 min
  52 s to create.
- **Record store and registry:** a Key Vault with RBAC, 7-day soft-delete retention, and no purge
  protection; a Basic registry with the server image and the provisioner image
  (`src/Atli.Reports.Provisioner/Dockerfile`), both by `az acr build`.
- **Provisioning service:** Container App `ca-provisioner`, 0.5 vCPU and 1 GiB, one replica, internal
  ingress only (HTTPS to port 8080), a termination grace period of 150 s, liveness on
  `/health/live` and readiness on `/health/ready`. Its own user-assigned identity holds Container
  Apps SandboxGroup Data Owner on the renderer group, Key Vault Secrets Officer on the vault, and
  AcrPull. `serve` with `Provisioner__*` settings: the renderer group, `Records:Store=KeyVault` with
  the identity's client ID, `Size=S` (0.5 vCPU, 1 GiB, one conversion, admits 2), disk image a
  (b from scenario 10), `NetworkConnection=renderers`, `AllowedSourceCidrs` the NAT gateway's `/32`
  alone, `AutoSuspendAfter=00:01:00`, the gateway's key in `Service:ApiKeys`, `TenantPrefixes`
  `appa-` (`MaxTenants` 60, `MaxCreatesPerMinute` 20) and `appb-` (5, 5), `MaxCreatesPerMinute` 40,
  `RetireCheckInterval` 1 minute, and `RetireAfterIdle` `00:00:00` (off; leftovers are still
  deleted) except during scenario 9, when it was 5 minutes. `DrainDelay` and `ReadyTimeout` kept
  their defaults (2 min 30 s, 3 min). Each change of settings or image made a new revision, ready
  30 to 82 s after its deployment started.
- **Gateway:** Container App `ca-gateway`, 1 vCPU and 2 GiB, one replica, external ingress, its own
  identity with AcrPull, Key Vault Secrets User, and the custom role with only
  `sandboxes/read` and `sandboxes/resume/action` on the renderer group (assigned as the deployment
  asks, though unused: the renderers' ports wake on request, so `Wake:Mode=None`). Two callers,
  `app-a` and `app-b`, with keys from `scripts/create-reports-api-key.sh` and the permissions
  `reports.convert` and `reports.tenants`, each with one prefix in `Tenants:<n>:TenantPrefixes`;
  `Provisioning:Mode=OnDemand` with the service's internal `https` address and its key;
  `MaxConcurrentRequestsPerCaller=64`. Everything else at its default: the record cache 30 s,
  `MaxConcurrentRequestsPerTenant` 8, `MaxNewTenantLookupsPerCallerPerSecond` 20,
  `Provisioning:Timeout` 1 min, `RendererTimeout` 90 s.
- **Client:** one sandbox in a group of its own without a network, `python-3.12`, 4 vCPU and 8 GiB,
  egress denied except to the gateway's host and the service's internal host (so that scenario 1
  tests the platform's ingress, not the client's egress policy), auto-suspend off. Its requests
  leave through the platform's egress proxy.
- **Operator:** the provisioner CLI on the laptop with the Azure CLI's login (Key Vault Secrets
  Officer on the vault; the group's creator holds Data Owner) for `list`, `disable`, and `enable`,
  which need no port, so the laptop's address was never in an allow-list. The rollout, whose
  readiness checks go through the renderers' ports, ran as a Container Apps job in the same
  environment with the service's identity and settings, so it reached the ports from the NAT
  gateway's address, which they admit.
- Fixtures as in the earlier runs: the invoice (69,600-byte PDF, 9,056-byte request) and the 49-page
  report (7,186,708 bytes, about 460 KB request), A4, 0.4 in margins, backgrounds, tagged PDFs.
- Timings come from the client (`time.monotonic()` around each request), the service's and the
  gateway's logs (their own timestamps), the data plane and Key Vault from the laptop, and Azure
  Monitor (the apps' CPU and memory, and the vault's API results, by minute).
- One region, one evening, one replica of each app; sample sizes are stated with each number.

## 1. Isolation through the gateway

From the client, one request each, as the caller named:

| Request | Answer |
|---|---|
| app-a converts for `appb-x`; app-b for `appa-x` | `403 Forbidden` (not a member) |
| app-a deletes `appb-x`; app-b deletes `appa-x` | `403 Forbidden` ("not under one of the caller's tenant prefixes") |
| app-a converts for, and deletes, `other-1` (under no prefix) | `403`, `403` |
| app-a converts for, and deletes, `contoso` (a listed-style ID, listed nowhere) | `403`, `403` |
| app-a converts for `appa-` (the prefix alone) | `403` |
| app-a converts for `readiness-probe` | `403` |
| app-a converts for `appa-Bad_Id` (not a tenant ID) | `403` |
| app-a deletes `appa-Bad_Id` | `400 InvalidRequest` |
| app-a converts without the tenant header | `400 InvalidRequest` |

Each answered in 10 to 26 ms. The gateway logged event 52 for each refused conversion and 69 for
each refused delete; the service logged nothing but its retirement runs, and no record or sandbox
appeared. This matches the docs: a tenant the caller does not own is a `403` for conversions and
deletes, and an invalid ID is a `400` for a delete.

**The service's internal address.** Its name (`ca-provisioner.internal.<environment domain>`)
resolves in public DNS, to the environment's public address, as the gateway's does. Requests to
it from outside the environment:

| Request | From the laptop | From the client sandbox |
|---|---|---|
| `GET https://<service host>/health/live` | `404` "Azure Container App - Unavailable" | `404`, the same page |
| To the environment's address, with the service's name as TLS server name and `Host` | `404`, the same page | `404`, the same page |
| To the gateway's name (TLS), with the service's name as `Host` | `404`, the same page | `421 Misdirected Request` |
| The gateway's own `/health/live`, for comparison | `200` | `200` |

So internal ingress keeps the service off the internet, whatever name or address is used; only the
gateway, inside the environment, reached it in this run.

## 2. A burst of new workspaces

app-a's first conversions of `appa-b01` to `appa-b40`, all 40 at once from the client, each on its
own connection; a `429` or `503` with `Retry-After` made that request wait as long and send again.

| | First wave | Second wave |
|---|---|---|
| Sent | t = 0 (20 passed the gateway's budget of 20 new tenants a second) | t = 60.5 to 61.1 s |
| Service: 20 creates started | 20:45:31.11 to 31.48 | 20:46:31.40 to 32.34 |
| Ready after the create call (median, range) | 3.3 s (1.4 to 3.9) | 4.2 s (1.1 to 4.7) |
| Records written | 20:45:32.76 to 35.24 | 20:46:32.76 to 37.16 |
| PDF at the client | 2.15 to 4.66 s | 62.21 to 66.58 s |

- **40 of 40 got a PDF, the last 66.6 s after the burst started.** 20 tenants needed one request,
  19 three, and one two; 79 requests in all.
- **The refusals were the documented ones.** 19 `503 Busy` from the gateway's budget of new-tenant
  lookups ("named more new tenants than the gateway looks up per second", `Retry-After: 1`, all at
  t = 0). The 21 that passed (the budget refilled during the burst) asked the service, which
  started 20 creates and refused the 21st with its rate limit for `appa-` (20 creates a minute); a
  second later the other 19 met the same limit. The gateway passed it on as `503 Busy` with the
  service's `Retry-After: 60`. The
  gateway logged 60 ensure calls (event 64), 40 renderers created (65), 20 refusals (66), and 19
  spent budgets (70); the service 40 creations and 20 rate-limit refusals.
- **Exactly 40 sandboxes and 40 records**, each record naming its tenant's one sandbox, with no
  duplicate and no leftover, by a listing of every page of the group (see
  [Bugs found](#bugs-found): the first census, by the data plane's first page, saw 25).
- Twenty creates at once in the connected group were ready 1.1 to 4.7 s after their create calls,
  within the range of single creates in the earlier runs (1.4 to 6.9 s).

## 3. Simultaneous first requests to one workspace

Ten requests for the new `appa-s01` at once.

- **One sandbox and one creation.** The gateway made one ensure call (event 64), the service created
  one renderer (ready 1.3 s after its create call), and the group had one sandbox labeled for the
  tenant, which its record names.
- **10 of 10 got a PDF.** Eight were admitted and got their PDF in 1.83 to 2.45 s. The other two
  met the gateway's limit for the tenant: with no record yet it is `MaxConcurrentRequestsPerTenant`
  (8), and once the record arrived it is the renderer's 2. They got `503 Busy` three times each,
  honoured `Retry-After: 1`, and got their PDF 3.22 and 3.29 s after the start.
- The eight admitted conversions reached a renderer that admits 2, so it must have refused some with
  `429`, which the gateway resends; none failed. (Event 56, each resend, is logged at `Debug`, which
  this run left off.)

## 4. Quota

app-b created `appb-q1` to `appb-q6` one after another.

- `appb-q1` to `q5`: `200` PDFs in 3.62, 1.70, 1.68, 1.59, and 1.61 s (each a new renderer).
- `appb-q6`: `503 BrowserUnavailable` in 0.08 s, "The renderer quota of the tenant's prefix is full.
  Delete tenants that are no longer used, or ask the operator to raise the quota.", with
  `Retry-After: 5`. A minute later, past the rate limit's window, the same answer. The service
  logged event 5 ("prefix appb- already has its most renderers (5)") each time.
- Exactly 5 records and 5 sandboxes under `appb-`.
- app-b then deleted `q2` to `q5` through the gateway (`204` in 0.41 to 4.69 s) to make room for
  scenario 5.

## 5. Fairness

For 70 s, app-a named new tenant IDs as fast as the client could send them: 32 workers in four
processes, each on its own connection, a new `appa-f…` ID on every request, honouring nothing.
Meanwhile, at a well-behaved pace:

- `a-warm`: app-a's `appa-b01` to `b03`, converted just before, every 5 s;
- `a-cold`: app-a's `appa-b04` to `b06`, untouched since the burst 28 minutes before (so the gateway
  had forgotten them, as it does a tenant that had a renderer after about ten minutes), every 5 s,
  honouring `Retry-After`;
- `b-existing`: app-b's `appb-q1`, every 2 s;
- `b-new-1` and `b-new-2`: app-b's new `appb-f1` from t = 10 s and `appb-f2` from t = 30 s, then every
  5 s, honouring `Retry-After`.

| Stream | Requests | Answers |
|---|---:|---|
| app-a's flood | 282,841 | 281,118 `503 Busy` from app-a's budget (p50 6 ms, p99 33 ms); 1,198 `503 Busy` from the service's rate limit (p50 45 ms); 198 `503` quota full; 21 PDFs (20 at t = 0, one at t = 60.1 s, when the rate window freed); 306 connections closed under the client (`SSLEOFError`, `RemoteDisconnected`) |
| app-a, recently used | 48 | 48 PDFs, p50 0.094 s, max 0.129 s |
| app-a, idle 28 minutes | 127 | 30 PDFs and 97 `503 Busy` from app-a's budget; their first PDFs at t = 3.0, 33.3, and 72.7 s |
| app-b, existing | 40 | 40 PDFs, p50 0.095 s; the first, waking the renderer, 1.59 s |
| app-b, new (2) | 24 | 24 PDFs; created in 1.70 and 1.85 s on their first request, no refusal |

- **The other application was unaffected.** app-b's existing workspace and its two new ones kept
  working through the flood, at their usual latency, with no refusal: each caller has a budget of
  its own in the gateway, and each prefix a rate limit and quota of its own in the service.
- **app-a's flood stopped at its own limits.** The gateway refused 99.4 % of it at once from app-a's
  budget of 20 lookups a second; what passed met the service's 20 creates a minute for `appa-`, and
  from t = 60.1 s app-a's quota of 60. The flood created 21 renderers, which brought app-a to
  exactly 60. One more quota answer came at t = 1.9 s, while app-a had 59; that was a bug, fixed
  since (see [Bugs found](#bugs-found)).
- **Key Vault was not throttled.** Over the run, the vault answered 1,475 not-found reads (909 in the
  busiest minute, 15 a second) and 452 found ones, and no `429`; the gateway logged no failed
  lookup. The budget held app-a's new-tenant lookups to about 20 a second.
- **But app-a's own idle workspaces waited.** A workspace the gateway has forgotten counts as new,
  and takes from its caller's budget; with the flood spending that budget, `appa-b05` got through
  only after the flood ended, at t = 72.7 s, and `b04` and `b06` at 3.0 and 33.3 s, each retrying
  every second. Workspaces app-a had used within ten minutes were not affected.
- **The flood cost the gateway its CPU and a lot of log.** The gateway's one vCPU ran at 0.996 cores
  for the busiest minute (0.187 mean over six minutes, 170 MiB). It wrote two lines per request,
  event 20 (each conversion request's end) and event 70 (each refusal from the budget): 566,827
  lines and 250 MB of ingested log in Log Analytics for the 70 s flood. The service's CPU stayed
  at 0.023 cores.
- 306 of the flood's requests, and none of the others, failed at the client with a connection the
  other end had closed, within milliseconds of sending, spread evenly over the 70 s; which hop
  closed them was not established.

`trim` then deleted the 21 workspaces the flood made, as app-a, two at a time (its limit), through
the gateway: 21 `204` in 0.41 to 0.76 s each.

## 6. Steady use

For 10 minutes, 30 workspaces each converted an invoice every 5 s, on a fixed schedule spread over
the interval, every 20th request the 49-page report: 20 existing ones (`appa-b07` to `b26`, all
stopped, so the first request of each woke it) and 10 new ones (`appa-n01` to `n10`, created by
their first request). Three more (`appa-b28`, `b29`, `b31`) converted every 180 s, which leaves each
renderer time to suspend (it did after 60 s idle, plus the time to suspend) before the next.

| Requests | Count | p50 | p95 | p99 | max |
|---|---:|---:|---:|---:|---:|
| Warm invoice | 3,390 | 0.086 | 0.129 | 0.170 | 0.587 |
| Warm 49-page report | 179 | 2.895 | 3.163 | 3.296 | 3.306 |
| First request, workspace created | 10 | 1.907 | 2.078 | 2.078 | 2.078 |
| First request, workspace woken | 20 | 1.564 | 4.231 | 8.240 | 8.240 |
| Every 180 s, first | 3 | 1.773 | | | 2.174 |
| Every 180 s, after 180 s idle | 7 | 1.254 | 1.621 | | 1.621 |

- **3,609 of 3,609 requests returned a PDF** (100 %), 6.0 a second.
- Warm invoices took 86 ms at the median, about what the earlier run measured for S (77 ms); the
  49-page report 2.9 s, as before.
- Woken workspaces took 0.64 to 8.24 s; three of twenty above 3 s (`appa-b24` 8.24 s, `b18` 4.23 s,
  `b26` 3.49 s), all among the first requests, which woke 20 renderers within 5 s. The earlier run's
  wakes in the connected group took 1.5 to 4.7 s.
- **The gateway and the service were idle.** Over the 10 minutes the gateway averaged 0.036 cores
  (0.051 at most in a minute) and 196 MiB; the service 0.003 cores and 207 MiB. Key Vault answered
  1,462 reads (the gateway refreshing 33 records every 30 s, the service listing) and 60 not-found
  ones, no `429`.

## 7. Delete

app-a deleted `appa-b32` to `appa-b36`, one after another, through the gateway.

- Five `204` in 0.59, 0.81, 0.44, 0.48, and 0.44 s. Each renderer was stopped; deleting a running
  one took 9.1 to 9.8 s (the smoke test and the reproduction in [Bugs found](#bugs-found)).
- Afterwards no record (no enabled secret) and no sandbox labeled for any of the five. The service
  logged, for each, the record deleted, then the sandbox, then event 8.
- The next conversion for `appa-b32` created a new renderer: `200` in 3.93 s. The service's log shows
  the renderer ready 1.3 s after its create call and the record written 2.3 s after that: writing the
  record of a deleted tenant first recovers its soft-deleted secret.

## 8. Kill switch

`appa-b37`, woken by a conversion first (2.89 s), then:

| Step | Result |
|---|---|
| `disable --tenant appa-b37` (laptop) | Exit 0 in 15.8 s ("Disabled sandbox … (Stopped)"); the data plane reports it `Stopped`, reason `Disabled` |
| app-a's `DELETE /tenants/appa-b37` | `409 InvalidRequest` in 0.09 s, "The operator disabled the tenant's renderer. The operator must enable or delete it before the tenant can be deleted."; the service logged event 13 and deleted nothing |
| Record and sandbox afterwards | Both still there, the sandbox still disabled |
| Two conversions, and a third 35 s later (past the record cache) | `503 BrowserUnavailable` "The tenant's renderer is not running." in 28 to 72 ms, `Retry-After: 5`; the gateway logged event 50 (not running, and it cannot wake it) |
| `enable --tenant appa-b37` | Exit 0 in 3.6 s; the sandbox stays stopped |
| Two conversions | `200` in 1.54 s (waking it) and 0.08 s |

With `Wake:Mode=None`, the on-demand port's answer for a disabled sandbox is the platform's "not
running", which the gateway does not try to wake. The disable reaches every sandbox of the tenant
only with the fixed listing; before it, the same command on the same tenant disabled nothing (see
[Bugs found](#bugs-found)).

## 9. Retirement

A new revision of the service with `RetireAfterIdle=00:05:00` and `RetireCheckInterval=00:01:00`
(ready 41.8 s after its deployment started, at 21:46:34). `appa-b38` converted every 20 s throughout
(started before the revision); `appa-b39`, `appa-b40`, and `appa-s01` converted once at 21:46:44
and then sat idle. The laptop listed the group and the vault every 30 s.

- **The first run, a minute after the revision started, retired 43 idle renderers in 20 s**
  (21:47:18 to 21:47:38): every tenant of both prefixes whose renderer had been stopped for more
  than 5 minutes, app-b's three included. Retiring a stopped renderer deletes its record and its
  sandbox, about 0.47 s each.
- **The watched workspaces** stopped (auto-suspend) at 21:47:50, 21:47:52, and 21:48:19, and were
  retired at 21:53:40 and 21:53:41, 5 min 22 s to 5 min 51 s after they stopped:

  ```text
  21:53:39.940 info: Atli.Reports.Provisioner.RendererProvisioner[30] [appa-b39] Retiring: sandbox 82eaddf6-75e3-4720-91ee-4ec806b312d2 has been stopped since 2026-10-04 21:47:52Z, longer than 00:05:00.
  21:53:41.717 info: Atli.Reports.Provisioner.Service.RetirementLoop[20] Retired 3 idle renderers and deleted 0 leftover sandboxes; 0 tenants failed.
  ```

- **Their next conversions created them again**: `200` in 4.16, 5.80, and 4.05 s, on disk a. They
  then sat idle again, stopped at 21:55:36 to 21:55:59, and were retired again at 22:00:42 to
  22:01:44.
- **The busy workspace was never retired**: 75 of 75 conversions returned a PDF, and its record named
  the same sandbox from start to end. No line of the service's log named it.
- `appa-b37`, enabled and used at 21:42 in scenario 8, was retired at 21:50:38. In all, 50
  retirements, no failure, and every run's listing covered the whole group.

## 10. Rollout that retires stopped renderers

With retirement off again and the service still on disk a, the service created eight renderers:
`appa-l01` to `l04`, kept running from then on by a light load (an invoice every 2 s each, for 15
minutes), and `appa-r01` to `r04`, left to auto-suspend. `appa-b38`, the busy workspace of
scenario 9, had stopped too. The service then moved to disk b (a revision ready 41.9 s after its
deployment started), so that retired tenants come back on it. Once `r01` to `r04` were stopped, the
job ran `rollout --disk-image <disk b> --stopped retire --max-parallel 4` in the environment, with
the service's identity and settings, from 22:19:18 to 22:22:25.

| Time | Rollout output (the job's log) |
|---|---|
| 22:19:49 (about 30 s after the job started) | "4 to replace, 5 stopped to retire, 0 already on it" |
| 22:19:49 to 22:19:55 | `appa-b38` and `r01` to `r04` retired: each record deleted, then its sandbox ("the tenant's next conversion creates a new renderer") |
| 22:19:49 to 22:19:55 | `l01` to `l04`: replacements created from disk b, ready 3.0 to 3.7 s after their create calls, records moved to them |
| 22:22:23 to 22:22:24 | The four old sandboxes deleted, after the 2 min 30 s drain |
| 22:22:25 | "Replaced 4, retired 5, already on the image 0, failed 0." and "No renderer sandboxes are left over." |

- **No request failed.** The light load's 1,800 requests all returned PDFs, 374 of them while the
  job ran; p50 0.086 s, p99 0.197 s, max 0.463 s. The gateway kept each cached record up to 30 s
  after it moved, and the old renderers answered through the drain.
- **Afterwards** `l01` to `l04` ran on disk b, with no old sandbox left; `r01` to `r04` and `b38` had
  neither record nor sandbox; `list` showed the four on disk b, and nothing else.
- **A retired workspace came back on disk b.** `appa-r01`'s next conversion: `200` in 6.46 s (a new
  renderer, and the recovery of its record's soft-deleted secret); its record names disk b, the
  service's `DiskImageId` since the move.
- The rollout created no renderer that would only sit idle: the stopped ones cost nothing until
  their workspaces convert again.

## 11. Leftover cleanup

A sandbox labeled `app=atli-reports`, `role=renderer`, `tenant=appa-leftover`, and `size=S`, as the
provisioner labels renderers, created through the data plane from disk a (S, egress denied,
auto-suspend 60 s), with no record.

- With the fixed listing (the second leftover, created 21:23:19): the service's retirement run found
  it at 21:29:50, 6.5 min after it was created, and deleted it at 21:32:21, 9.0 min after. That is
  the documented protection (created more than the drain plus `ReadyTimeout`, 5.5 min, ago), the
  next run (every minute), and the drain (2.5 min) before the delete:

  ```text
  21:29:50.294 info: Atli.Reports.Provisioner.RendererProvisioner[30] [appa-leftover] Sandbox 563d749d-3546-498b-930f-bc8bbb937dd2 is a renderer no record points to.
  21:32:21.042 info: Atli.Reports.Provisioner.RendererProvisioner[30] [appa-leftover] Deleted the leftover sandbox 563d749d-3546-498b-930f-bc8bbb937dd2.
  ```

- The first leftover (created 20:45:19, before the burst) was never seen by the service at
  `f03e90e`: thirteen runs from 20:51 to 21:04 reported "deleted 0 leftover sandboxes", since it was
  not on the data plane's first page of 25. The service at `1d108c9` found it on its first run,
  21:05:48, and deleted it at 21:08:18.

## Bugs found

Three, each fixed as its own commit with tests, on `ws-azure`:

1. **The provisioner saw only the first 25 sandboxes of a group** (`1d108c9`,
   `fix(hosting): list every sandbox of a group, page by page`). `SandboxesClient.ListAsync` read
   `GET sandboxes` on api-version `2026-02-01-preview` as one unpaged array. That version answers
   with one page: 25 sandboxes by default, at most 100 with `pageSize`, and no link to the rest (it
   ignores `skipToken`). `2026-09-01-preview` pages with `nextLink`. With 41 sandboxes in the group,
   reproduced with the provisioner at `f03e90e`:
   - `disable --tenant appa-b37`, the kill switch, printed "No sandbox to disable." and exited 0;
     the renderer stayed `Stopped (Idle)`, not disabled.
   - app-a's `DELETE /tenants/appa-b30` through the gateway answered `204` and deleted the record,
     but the service left the sandbox, and its memory snapshot, `Stopped`. (Deletes of six newly
     created tenants worked; new sandboxes appear to come first in the listing.)
   - `list` showed 15 of 40 tenants as `missing`; the service's retirement runs did not see the
     first leftover for 13 minutes; a rollout would have replaced every renderer it did not see.

   The client now lists on `2026-09-01-preview` in pages of 100 and follows `nextLink`, which must
   keep the first page's scheme, host, port, and path (the token goes with it); a link already
   followed, more than 1,000 pages, or a bare array as long as a page fails the listing rather than
   passing for the whole group. Re-checked on Azure: `list` showed no tenant `missing`, `disable` of
   the same tenant (still off the first page) disabled it, and app-a's delete of `appa-b27`, off the
   first page, removed its record and its sandbox. Scenarios 3 to 11 ran with the fix.
2. **A creation that ended could count its tenant twice toward the quota** (`5b23334`,
   `fix(provisioner): count a finished creation once toward its prefix's quota`). The service told
   its census about the new record, then logged, then stopped counting the creation as in flight. A
   quota check in between counted the tenant twice. In scenario 5 one new tenant was refused as over
   the quota while app-a had 59 of 60, in the millisecond a creation ended. Both now happen in one
   step under the lock the quota is checked under; the new test asks for a second tenant from the log
   entry of the first one's creation, the point the two steps used to straddle. Not re-measured on
   Azure, where the race is rare; the service ran the fix in scenario 10.
3. **The server image depended on the builder's umask** (`667be3d`,
   `fix(server): give the image's files fixed modes, whatever the builder's umask`). The first setup
   extracted the build context under umask 077; publish copied `appsettings.json` into `/app` as
   `0600` root, and the gateway, which runs as UID 1654, exited with code 134 at every start until
   the revision's progress deadline. The renderers' disk images had the same defect. The build
   stage now gives `/app` a default checkout's modes, and the image workflow builds from a checkout
   whose files only their owner may read, so its smoke test catches a regression. Checked locally:
   from such a context, the image before the change exits with `UnauthorizedAccessException`; after
   it, it answers `/health/live` and `/health/ready`. The run itself rebuilt the gateway's image and
   both disks from `f03e90e` with the context's modes normalized.

The harness had its own: a census that used the first page too (fixed before the scenario 2
verdict), and a test callback that read the test's context on a thread without it.

## What else the run found

- **One application's flood costs everyone's gateway CPU and log budget.** The budget kept Key Vault
  safe and app-b unaffected, but at 4,000 requests a second the gateway's single vCPU was saturated
  and it logged two lines per request, 250 MB for 70 s. Kept up, that is about 13 GB of log an hour
  from one caller, and a second replica would only double the log.
- **An application's flood delays its own idle workspaces.** A workspace not used for about ten
  minutes counts as new, so it waits for its caller's budget like the flood's IDs: up to 72.7 s here.
- **The quota answer says `Retry-After: 5`**, the default of `BrowserUnavailable`, though the quota
  lasts until tenants are deleted or retire; a client that honours it retries every 5 seconds.
- **The service's internal name is in public DNS.** It resolves to the environment's public address;
  ingress refuses every request from outside (`404`, or `421` with the gateway's TLS name).
- **Recreating a deleted tenant takes about 2.3 s longer**, while the vault recovers the record's
  soft-deleted secret before writing it.
- **Deleting a stopped renderer takes 0.3 to 0.8 s, a running one 9 to 10 s**, by the gateway's
  `DELETE`; so retiring stopped renderers is fast (43 in 20 s).
- **A tenant without a record admits 8 conversions at once in the gateway**, its default limit, then
  2 once the record of its S renderer is read; the burst beyond 8 gets `503 Busy`.

## What this does not show

- More than one replica of the gateway or the service; their limits are per replica.
- JWT callers, more than two applications, or an application's own check of its users.
- Groups of hundreds or thousands of renderers: the paged listing was exercised with at most 61
  sandboxes (pages of 100 on Azure; the tests page by hand).
- The census at thousands of records, or Key Vault throttling under several flooding callers.
- A rollout of more than a few renderers, or a release whose image differs (disk b was built from
  the same commit as a).
- Retirement over days (`RetireAfterIdle` was 5 minutes), or a renderer woken in the window between
  retirement's last read and its delete.
- Private ingress to the renderers; their ports are public endpoints gated by the NAT gateway's
  address and per-renderer keys.
- Cost from the bill: the figure below is an estimate from list prices.

## Cleanup

- `workspaces.py cleanup` (22:38 to 23:11 UTC) deleted the rollout job, the gateway, and the
  provisioning service; the 9 role assignments at scopes inside the group (the two identities' six,
  the operator's Key Vault Secrets Officer, and the Data Owner assignments the `aca` CLI makes for a
  sandbox group's creator); the custom role definition; the Log Analytics workspace, with `--force`
  so it is not kept soft-deleted; the Container Apps environment, which removed the infrastructure
  group; the client sandbox group (16.5 s) and the renderer group with its network connection
  (33.0 s); and the resource group, gone 30 minutes after its delete started. The Key Vault, which
  went to soft-delete with its group, was purged.
- Checked afterwards, at 23:11 UTC and again by hand: `az group exists` answers `false` for both
  groups, and no `rg-atli-reports-workspaces-*` (or any `rg-atli-reports*`) group remains; no
  resource carries the `purpose=atli-reports-workspaces` tag; no role assignment's scope is inside
  the group, and none belongs to either identity; the custom role definition is gone, by ID and by
  name; both identities are gone; the vault is neither active nor soft-deleted; the workspace is not
  among the deleted workspaces.
- Every log of the run, the client's request records, the results, and the committed files were
  searched for 129 values: the two callers' keys and verifiers, the gateway's key for the service,
  every renderer credential the run read, bearer tokens, the subscription and tenant IDs, the
  operator's object ID, both identities' client and principal IDs, the workspace's ID, the
  gateway's and the service's hosts, the environment's domain and address, the registry's host, the
  disk images' IDs, this machine's public address, the NAT gateway's, and the client's ten outbound
  addresses; and for API-key and token patterns, port and Container Apps hosts, and any public IPv4
  address. None was found. The work directory (keys, state, logs, request records), the scratch
  files, the local test images, and the temporary `aca` CLI were removed afterwards.
- Spend, estimated from list prices (the bill arrives later): about $4, mostly the renderers (about
  12 renderer-hours at S, $0.054 an hour), the client sandbox (2 hours at 4 vCPU and 8 GiB), the
  log ingested (about 0.4 GB, most of it the flood's), and the two apps, against a target of $25.
