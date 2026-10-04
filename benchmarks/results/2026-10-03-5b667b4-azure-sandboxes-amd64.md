# Azure Container Apps Sandboxes as a renderer — amd64, 2026-10-03

This run put the server image, unmodified, in
[Azure Container Apps Sandboxes](https://learn.microsoft.com/en-us/azure/container-apps/sandboxes-overview)
as a per-customer renderer of the [hosted renderer design](../../docs/hosted-renderers.md). It
checks Chromium's sandbox inside the microVM and measures conversions, cold starts, suspend and
resume, snapshots, egress, and three sizes. The
[design](../../docs/hosted-renderers.md#azure-container-apps-sandboxes) records what it changes.

Raw values: [`2026-10-03-5b667b4-azure-sandboxes-amd64.json`](2026-10-03-5b667b4-azure-sandboxes-amd64.json)
(run 3). Reproduce with [`benchmarks/azure-sandboxes/run.py`](../azure-sandboxes/run.py), which
creates a resource group of its own and deletes it afterwards.

## Conditions and limits

- Region `eastus2`; `aca` CLI `1.0.0-preview.4`; data-plane API `2026-02-01-preview`. Each sandbox
  is a Cloud Hypervisor microVM: guest kernel `6.12.8+`, `x86_64`, Intel Xeon Platinum 8573C.
- The disk image was built by the platform from the server Dockerfile at `5b667b4`
  (`aca sandboxgroup disk create --source`; Chrome for Testing 154.0.8037.92). The harness files
  were not yet committed (`dirty` in the JSON); the server sources are those of `5b667b4`.
- Renderer: 2 vCPU and 4 GiB unless a table says otherwise, `--egress-default Deny`, the image's
  user (1654), entrypoint `tini -- /app/Atli.Reports.Server`, API-key authentication with a key
  generated for the run, and port 8080 exposed anonymously through the platform's proxy.
- Three vantage points:
  - **In the VM:** [`convert-client.pl`](../azure-sandboxes/convert-client.pl) over loopback, timed
    with `CLOCK_MONOTONIC`; no network outside the VM.
  - **In the region:** a second sandbox (1 vCPU, Ubuntu) running `curl` against the renderer's
    port URL, a new TLS connection per request. That client's own egress also passes the
    platform's egress proxy.
  - **The laptop:** lifecycle REST calls with one cached token, readiness polling, and the first
    request after a resume. The laptop's round trip to the proxy was about 0.2 s, and during run 3
    it was under heavy unrelated load (load average about 400), so laptop-side times are upper
    bounds.
- Fixtures and options as in the [load benchmark](../README.md#fixtures): A4, 0.4 in margins,
  backgrounds, tagged PDFs, and `waitForSignal: "reportReady"` for `chart`.
- One warmup, then five samples; values are medians unless stated. Sizes in MB (10^6 bytes).
- Two harness runs (run 2 at 22:44 and run 3 at 23:28 UTC) plus exploratory probes. Run 2 timed
  lifecycle operations through the `aca` CLI, which fetches a token per command; run 3 timed them
  over REST. Samples are small, from one region on one evening.

## 1. Chromium's sandbox and the VM

| Check | Result (runs 2 and 3) |
|---|---|
| `chromium-sandbox-check.sh` (as in the Kubernetes validation) | Passes: no `--no-sandbox`; two zygotes outside the browser's user namespace |
| Renderer processes during a conversion | `Seccomp: 2`: Chromium's own seccomp-bpf filter |
| Browser, zygote, GPU, and network processes | `Seccomp: 0` |
| `seccomp-probe.pl chromium` | 8 of 13 cases differ from `chromium.json`: `clone3` returns `EINVAL` rather than `ENOSYS`, and every namespace combination is allowed. There is no container-level seccomp filter; the kernel those calls reach is the renderer's own. |
| Server process | User 1654, `Seccomp: 0`, `NoNewPrivs: 0` |

## 2. Warm conversion time

Seconds, median of five (min–max for the 2 vCPU in-VM column).

| Fixture | 2 vCPU, 4 GiB, in the VM | 2 vCPU, 4 GiB, through the proxy | 1 vCPU, 2 GiB, in the VM | 0.5 vCPU, 1 GiB, in the VM |
|---|---:|---:|---:|---:|
| invoice | 0.038 (0.034–0.041) | 0.058 | 0.061 | 0.065 |
| chart | 0.067 (0.063–0.069) | 0.080 | 0.071 | 0.076 |
| long-table (49 pages, 7.19 MB) | 2.10 (2.00–2.14) | 2.09 | 2.05 | 2.88 |

Run 2 agreed within about 0.1 s: in the VM 0.038, 0.063, and 1.98 s; through the proxy 0.063,
0.081, and 2.01 s; 2.06 s at 1 vCPU and 2.76 s at 0.5 vCPU for the long report. `/health/live`
through the proxy took 0.016 s (run 2: 0.025 s). For comparison, the warm runc worker on the ARM64
laptop VM took 1.90 s for the same report
([follow-up probes](2026-10-02-b88e5b5-isolation-followup-arm64.md)).

Memory of the whole VM (`MemTotal - MemAvailable`, MiB), sampled every 50 ms during the 49-page
report:

| Size | Before | Peak | VM total |
|---|---:|---:|---:|
| 2 vCPU, 4 GiB | 379 | 854 | 4163 |
| 1 vCPU, 2 GiB | 268 | 759 | 2218 |
| 0.5 vCPU, 1 GiB | 305 | 660 | 1213 |

The warm runc control's cgroup peak after the same report was about 865 MiB in the
[recorded worker run](2026-10-02-5c500701-isolated-workers-arm64.md).

## 3. Lifecycle

Run 3, REST calls from the laptop. A data-plane `GET` took 0.44 s there, against 3.2 s for
`aca sandbox get`.

New renderers (2 vCPU, 4 GiB), three in a row:

| | 1 | 2 | 3 |
|---|---:|---:|---:|
| Create call (returns `Running`) | 1.23 | 0.87 | 0.87 |
| Port call | 0.20 | 1.70 | 0.21 |
| `/health/ready` after the create call started | 1.87 | 2.88 | 1.40 |
| First invoice, from the laptop | 0.49 | 0.54 | 0.40 |

The run's first renderer was ready 5.23 s after its create call; the 1 vCPU and 0.5 vCPU renderers
after 3.77 and 1.37 s. Run 2, timed through the CLI, saw 2.4 to 5.6 s at 2 vCPU and 20.5 and
16.0 s for the smaller sizes.

Suspend (memory mode) and resume of one renderer:

| Cycle | Stop until `Stopped` | Resume call | Resume start to first PDF (laptop) | Next invoice (laptop) |
|---|---:|---:|---:|---:|
| 1 | 6.77 | 1.24 | 1.74 | 0.28 |
| 2 | 14.78 | 1.48 | 1.86 | 0.29 |
| 3 | 14.00 | 1.24 | 1.75 | 0.29 |

- While stopped, a request got `403 {"error":"Sandbox is not running"}`: nothing resumes a
  sandbox on request.
- After every resume the first conversion succeeded on its first attempt, with the browser still
  running.
- With auto-suspend set to 60 s idle, the renderer was observed `Stopped` 92.8 s after its last
  request (run 2: 98.2 s), with the browser running throughout.
- Granting the creator data-plane access took 17 s in run 2 and 805 s in run 3. Deleting a
  sandbox group took about ten minutes.

## 4. Snapshots

| | Run 3 | Run 2 | Exploration |
|---|---:|---:|---:|
| Snapshot of the warm renderer (s) | 8.9 | 25.8 | 5.5 |
| Snapshot size (MB) | 201 | 197 | 197 |
| Sandbox from the snapshot, create (s, CLI) | 3.6, 11.3 | 13.6, 234.2 | 3.0, 2.8 |

In every case, the sandboxes started from the snapshot had the same browser executable, libc,
and stack addresses as the original, and its environment, including the API key's verifier and ID.
The kernel's boot ID and `/dev/urandom` output differed per sandbox.

## 5. Network and identity

From inside the renderer ([`network-probe.sh`](../azure-sandboxes/network-probe.sh)):

| Probe | Result |
|---|---|
| HTTP `GET http://example.com/` | `403` from the platform's egress proxy |
| TCP to `1.1.1.1:443` | Connection accepted by the egress proxy, which refuses the request itself |
| TCP to `8.8.8.8:53` | Blocked |
| TCP to `169.254.169.254:80` (instance metadata) | Blocked |
| DNS lookup of `example.com` | Resolves (Azure DNS, `168.63.129.16`) |
| Managed-identity endpoint (`IDENTITY_ENDPOINT`) | `401 unauthorized_client`: the sandbox group has no identity |

The egress decision log listed the denied `example.com` request in run 2. In run 3, queried
seconds after the probe, it was still empty.

## 6. Cost

Sandboxes are billed at the Container Apps consumption rates (Azure retail prices API, `eastus2`,
2026-10-03): $0.000024 per vCPU-second and $0.000003 per GiB-second while running. That is $0.216
per running hour at 2 vCPU and 4 GiB, $0.108 at 1 vCPU and 2 GiB, and $0.054 at 0.5 vCPU and
1 GiB. A suspended renderer pays for its snapshot at Premium Blob ZRS rates, $0.20 per GB-month:
about $0.04 a month for these 0.2 GB snapshots. Microsoft lists that storage charge as coming soon.

## What this does not show

- Concurrent load, throughput, or tail latency.
- Private ingress through a virtual network, or Entra-authenticated service access to a port.
- A custom role limited to `sandboxes/read` and `sandboxes/resume/action`.
- Other regions, quotas, or behaviour over days.
- Azure Container Apps apps or dynamic sessions; only Sandboxes were measured.
