# Hosted renderers end to end on Azure Container Apps Sandboxes — amd64, 2026-10-04

This run puts the whole [hosted renderer](../../docs/hosted-renderers.md) path together on real
Azure Container Apps Sandboxes: the [provisioner](../../src/Atli.Reports.Provisioner/README.md)
creates per-tenant renderers from a disk image built from this commit's server Dockerfile, and the
server in [gateway mode](../../docs/engine/server.md#gateway-mode), run on a laptop, authenticates a
caller and routes its conversions to those renderers. It checks waking on demand and through the
gateway's resume, the ports' source-address allow-list, a rollout under load, the kill switch,
deleting a tenant, and cleanup.

Raw values: [`2026-10-04-b9c51da-hosted-renderers-e2e-amd64.json`](2026-10-04-b9c51da-hosted-renderers-e2e-amd64.json)
(run 2). Reproduce with [`benchmarks/azure-sandboxes/e2e.py`](../azure-sandboxes/e2e.py)
(`e2e.py all`), which creates a resource group of its own and deletes it afterwards.

## Conditions and limits

- Region `eastus2`. `aca` CLI `1.0.0-preview.4`; data-plane API `2026-02-01-preview`.
- Two runs, each in a new resource group `rg-atli-reports-e2e-<UTC timestamp>` tagged
  `purpose=atli-reports-hosted-e2e`:
  - **Run 1** at `f31ea23` (the integration branch), 03:41 to 03:56 UTC, with an earlier
    `e2e.py` that had no check h and no verdicts. It found the two gateway bugs under
    [Bugs found](#bugs-found).
  - **Run 2** at `b9c51da` (`f31ea23` plus the two fixes), 04:08 to 04:20 UTC, with `e2e.py all`
    as committed, apart from comments and a retry of failed sandbox reads added afterwards. Unless
    a section says otherwise, numbers are run 2's.
- Each run built two disk images remotely from its commit's server Dockerfile
  (`aca sandboxgroup disk create --source` on a `git archive` of the build context, as
  [`run.py`](../azure-sandboxes/run.py) does), `reports-server-<sha>-a` for the tenants and `-b`
  as the rollout target, in parallel: 152.9 and 153.1 s (run 1), 152.9 and 163.1 s (run 2). The
  data-plane role that `aca sandboxgroup create` grants worked after 16.4 s (run 1) and 46.9 s
  (run 2).
- Provisioner and gateway: Release builds of the run's commit, run with `dotnet` on the laptop
  (macOS, arm64) and configured by `Provisioner__*` and `ReportsServer__*` environment variables.
  - Provisioner: `Size=S` (0.5 vCPU, 1 GiB, one conversion, admits 2), `AutoSuspendAfter=00:01:00`,
    `AllowedSourceCidrs` = the laptop's public IPv4 `/32` only, `PortActivation=OnDemand`, and
    `Manual` for the one `create --tenant globex` command. Records in a `File` store (a `0700`
    directory) that the gateway reads too.
  - Gateway: `Mode=Gateway` on `127.0.0.1`, API-key authentication with one caller `e2e-app`
    (`reports.convert` only, key from `scripts/create-reports-api-key.sh`) in tenants `acme` and
    `globex`, record cache 30 s, `Wake:Mode=Sandboxes` with the default credential chain, which
    used the Azure CLI login. That login holds the group's Data Owner role, not the resume-only
    role the design gives the gateway.
- Fixtures as in the first Sandboxes run: the invoice (69,600-byte PDF) and the 49-page report
  (7.19 MB), A4, 0.4 in margins, backgrounds, tagged PDFs.
- Latencies are measured by the client on the laptop, next to the gateway; the gateway's own logged
  durations agreed within a few milliseconds. The laptop reaches the eastus2 port proxy over the
  internet (a warm invoice took 0.11 to 0.17 s end to end, of which about 0.07 s is the S
  renderer's conversion), so these are not in-region latencies.
- Small samples, one region, one evening.

## Results

| Check | Run 1 (`f31ea23`) | Run 2 (`b9c51da`) |
|---|---|---|
| a. Conversions and refusals through the gateway | Pass | Pass |
| b. OnDemand wake after auto-suspend | Pass | Pass |
| c. The gateway's resume for a Manual port | Pass | Pass |
| d. The port's source-address allow-list | Pass | Pass |
| e. Rollout under load | Pass: 168 of 168 requests `200` | Pass: 167 of 167 requests `200` |
| f. Kill switch | `503`, but each request held 30 s | Pass: `503` in 0.08 to 0.28 s |
| g. Deleting a tenant | `500 RenderFailed` for 16.6 s, then `503` | Pass: `503` from the first request |
| h. A port refusing the gateway's address | Not run | Pass: `503`, logged as an error |
| Provisioner deletes the last tenant; nothing left | Pass | Pass |
| No secret in any log or in the results | Pass | Pass |

## a. Tenants, conversions, and refusals

`create` took 2.92 s for `acme` and 2.79 s for `globex`, each ready 2.8 and 2.7 s after its create
call; `list` showed both `Running`, size `S`, on disk image a. Each record admits 2 requests at once.
Both ports were anonymous, deny-by-default with one allow rule, `OnDemand` for `acme` and `Manual`
for `globex`. The gateway answered `/health/ready` 0.29 s after it started.

| Request through the gateway | Result |
|---|---|
| `acme` invoice, three in a row | `200` PDF in 0.53, 0.17, 0.12 s |
| `acme` 49-page report | `200` PDF (7.19 MB) in 3.64 s |
| `globex` invoice, three in a row | `200` PDF in 0.48, 0.15, 0.11 s |
| `globex` 49-page report | `200` PDF in 4.10 s |
| No `X-Reports-Tenant` (the caller has two tenants) | `400 InvalidRequest` |
| `X-Reports-Tenant: initech` | `403 Forbidden` |
| A wrong caller key; a blank one | `401 Unauthorized` each |

Straight to `acme`'s port URL from the laptop (an allowed address), the renderer itself refused a
request without a key and one with the caller's key (`401` each, 0.17 s): it admits only its own
credential. Run 1 matched: the first invoices in 0.54 and 0.67 s and the next ones in 0.15 to
0.17 s, the reports in 3.55 and 3.89 s, and the same refusals.

## b. Waking on demand

With auto-suspend at 60 s, the renderers were observed `Stopped` (reason `Idle`, polled every 2 s)
125.3 s (`acme`) and 128.0 s (`globex`) after their last request, and `acme` again after 142.1 s
(run 1: 120.2, 144.5, and 148.4 s).

| `acme`, stopped, through the gateway | Run 1 | Run 2 |
|---|---:|---:|
| Invoice as the waking request | 0.97 s | 1.74 s |
| 49-page report right after it | 3.70 s | 3.55 s |
| Next invoice | 0.12 s | 0.13 s |
| 49-page report as the waking request (after the next auto-suspend) | 4.28 s | 5.48 s |

Every request was a complete PDF; the gateway logged no wake events, since the port proxy resumed
the sandbox itself.

## c. Waking through the gateway (Manual port)

`globex` was stopped. A request straight to its port answered
`403 {"error":"Sandbox is not running"}` in 0.17 s and left it `Stopped`. Through the gateway the
invoice then took 3.38 s (run 1: 4.35 s), and the next 0.16 s. The gateway's log, in order:

```text
04:14:28.619 info: Atli.Reports.Server.Gateway.SandboxWaker[60] Resuming sandbox 664be9fe-782c-438a-9454-b0320af5070f, the renderer of tenant globex.
04:14:29.046 info: Atli.Reports.Server.Gateway.SandboxWaker[61] Sandbox 664be9fe-782c-438a-9454-b0320af5070f, the renderer of tenant globex, resumed (Running) in 426.5884 ms.
```

The request arrived at 04:14:26.040, so about 2.4 s went to the state read before the resume, most
of it the gateway's first data-plane token from the Azure CLI; the resume took 0.43 s and the
resent conversion 0.37 s. In run 1 the same steps took 1.8, 1.63, and 0.9 s.

## d. The source-address allow-list

From a client sandbox in the region (public `ubuntu` disk, egress denied except `*.adcproxy.io`),
with `acme` stopped, `POST /convert` and `GET /health/live` to `acme`'s port URL (read from its
record) both answered
`403 {"error":"Access denied by IP access control policy","errorCode":"IpAccessDenied"}`, the first
in 0.051 s. `acme` was still `Stopped` 10 s later: refused requests wake nothing. Run 1 was the
same.

## e. Rollout under load

A loop converted an invoice for `acme` through the gateway every 0.5 s, one request at a time,
from 5 s before `rollout --disk-image <disk b> --drain 00:00:40` until 35 s after it returned. The
rollout took 44.53 s (run 1: 45.16 s); seconds from its start:

| Time | Rollout output |
|---:|---|
| 1.73 | 2 to replace, 0 already on the image, 4 at a time |
| 1.85, 1.90 | Creating replacements for `acme` and `globex` (`globex` stopped, replaced without a resume) |
| 3.07 | Both ready, 1.2 s after their create calls |
| 3.08 | Both records point to the new sandboxes |
| 43.47, 44.45 | Old `globex` and `acme` sandboxes deleted after the 40 s drain |
| 44.53 | No leftover renderer sandboxes; replaced 2, failed 0 |

- **Every request succeeded:** 167 of 167 `200` with a PDF (run 1: 168 of 168), median 0.16 s,
  slowest 0.48 s. The gateway kept its cached record for up to 30 s after the move, so it used the
  old renderer in that time and the new one afterwards; the old one was deleted only after both.
- Both records name new sandboxes on disk image b; both old sandboxes no longer exist;
  `prune --drain 00:00:00` found nothing to delete; `list` showed `acme` `Running` and `globex`
  already `Stopped` again, both on disk image b.
- The replacement `globex` port was `OnDemand`, not `Manual`: a rollout creates renderers with the
  current `Provisioner:PortActivation`, and the run set `Manual` only for `globex`'s `create`. The
  later `globex` request in section g woke it on demand (1.41 s) with no gateway resume.

## f. Kill switch

`disable --tenant acme` took 5.2 s (run 1: 5.99 s). The sandbox was then `Stopped` with reason
`Disabled`, and its port answered `403 {"error":"Sandbox is not running"}`.

| Conversion for `acme` | Run 1 | Run 2 |
|---|---|---|
| First after `disable` | `503` after 30.13 s: "did not wake in time" | `503` after 0.28 s: "not running" |
| Next two | `503` after 30.29 and 30.13 s | `503` after 0.08 and 0.12 s |
| Gateway log | A 409 `SandboxAdminDisabled` resume failure (event 62, with a stack trace) every 5.5 s, then event 51 | Event 59, "is disabled", per request; no resume call |

`enable --tenant acme` took 0.83 s (run 1: 1.89 s), and the next conversion woke the renderer on
demand and succeeded in 0.62 s (run 1: 1.31 s), then 0.16 s.

## g. Deleting a tenant

A `globex` conversion right before the delete put its record in the gateway's cache.
`delete --tenant globex` took 7.64 s (run 1: 9.66 s): the record first, then the sandbox. Then a
`globex` conversion every 2 s for 45 s:

| Seconds after the delete | Run 1 | Run 2 |
|---|---|---|
| 0.06 | `500 RenderFailed`: "could not convert the document" | `503 BrowserUnavailable`: "unavailable"; event 57 |
| 2 to 16.6 | `500 RenderFailed`, 8 more | `503`: "The tenant has no renderer." |
| 18.6 to 45 | `503`: "The tenant has no renderer." | The same |

Run 1's `500`s came from the port proxy's `404 {"error":"Not found"}` for the deleted sandbox,
which the gateway treated as a document failure until its cached record expired. `list` then
showed only `acme`, whose next conversion succeeded (0.37 s).

## h. A port that refuses the gateway's address

`acme`'s port was removed and added again as the provisioner adds it, but allowing only
`203.0.113.0/24`. Two conversions through the gateway answered `503 BrowserUnavailable` in 0.05 s
each, and the gateway logged an error for each:

```text
04:19:48.000 fail: Atli.Reports.Server.Gateway.RendererGateway[58] The port of tenant acme's renderer (sandbox 466aa398-f66f-4093-9636-a894941c09aa) refused the gateway's address (IpAccessDenied). Its allowed source ranges must include the gateway's outbound addresses.
```

At `f31ea23` this answer would have been a `500 RenderFailed`, logged at `Information` as event 45.

## Bugs found

Both are fixed on this branch, with tests, and were checked again in run 2.

1. **A deleted renderer was a `500` document failure, and its record stayed cached**
   (`e1de456`). For a sandbox that no longer exists, the port proxy answers
   `404 {"error":"Not found"}`. The gateway mapped that to `500 RenderFailed` and kept the record
   until its cache expired, although its docs say a record naming a sandbox that no longer exists
   is dropped. A port refusing the gateway's address (`403` with `errorCode` `IpAccessDenied`) was
   a `500 RenderFailed` too. Both are now `503 BrowserUnavailable`; the `404` evicts the record
   (event 57) and the refusal is logged as an error (event 58).
2. **A disabled renderer held every request for the whole wake window** (`b9c51da`). With
   `Wake:Mode=Sandboxes`, the not-running answer from a disabled sandbox's port made the gateway
   resume it, which the platform refuses with `409 SandboxAdminDisabled`, and retry for 30 s. The
   data plane reports a disabled sandbox as `Stopped` with `stateDetails.stoppedReason` `Disabled`
   (and `UserStopped` once enabled, checked by hand on a separate sandbox), so the gateway now
   reads that with the state it already checks, answers `503` at once, and logs event 59.

## What this does not show

- The gateway running in Azure, the source address it would present to the allow-list, the
  Key Vault record store, managed identities, and the resume-only custom role: the gateway here
  ran on a laptop with the Azure CLI login, which holds Data Owner.
- In-region latencies; the laptop's internet path dominates every small request here.
- More than one gateway replica, sustained or concurrent load, and sizes M and L.
- A rollout while long conversions are in flight, or with a drain shorter than the record cache;
  rollouts of more than two tenants; canceled commands, and `prune` with real leftovers.
- Private ingress, a virtual network, or a group that blocks DNS. Chromium's sandbox in the
  renderer was not checked again; the [first Sandboxes run](2026-10-03-5b667b4-azure-sandboxes-amd64.md)
  did.

## Cleanup

- In each run, after the last check the gateway was stopped, the client sandbox deleted, and the
  provisioner deleted the remaining tenant (`delete --tenant acme`, 3.0 s); `list` then answered
  "No renderers." and the group held no sandbox labeled as a renderer.
- `cleanup` then deleted the sandbox group (16.3 and 16.6 s) and the resource group. Both are gone:
  `az group exists` answered `false` at 04:08:51 UTC (run 1) and 04:20:12 UTC (run 2), `az group show`
  answers `ResourceGroupNotFound` for both, no `rg-atli-reports-e2e-*` group remains, no resource
  carries the `purpose=atli-reports-hosted-e2e` tag, and no role assignment is scoped to either
  group.
- The check by hand in bug 2 used one `ubuntu` sandbox in run 1's group, deleted before that group.
- Every provisioner and gateway log (and in run 2 the results file) was searched for the caller's
  key, a wrong key, every renderer credential (four per run), the laptop's public address, and the
  subscription ID: none was found. The temporary work directories (keys, records, logs) and the
  temporary `aca` CLI were removed afterwards.
