# Benchmarks

A reproducible comparison of **Atli.Reports** with **[Gotenberg](https://gotenberg.dev) 8** (Chromium
route) under identical conditions, plus in-process micro-benchmarks of the `Atli.Reports.Engine`
API. One command runs everything and writes a dated results file you can commit and compare.

```bash
benchmarks/run.sh                  # quick: all fixtures, concurrency 1 and 16 (~6 min)
benchmarks/run.sh --full --micro   # full: concurrency 1, 4, 16, 64 (~25 min) + micro-benchmarks (~3 min)
benchmarks/run.sh --help           # every option
```

Requirements: Docker with Compose v2 (Docker Desktop, OrbStack, or Docker Engine) and the .NET SDK
from `global.json`. Nothing else is installed on the host. Works on macOS (bash 3.2) and Linux,
arm64 and x86-64. `--micro` also needs Chrome or Edge installed on the host.

**Latest results:** [`results/2026-10-01-3829bfd.md`](results/2026-10-01-3829bfd.md), engine
3829bfd against Gotenberg 8.37.0 in full mode, with the micro-benchmarks in
[`results/2026-10-01-3829bfd-micro.md`](results/2026-10-01-3829bfd-micro.md).
[`results/2026-10-01-287f9bc.md`](results/2026-10-01-287f9bc.md) is the provisional baseline of the
engine before it kept one long-lived browser.

The separate [isolated-worker exploration](results/2026-10-02-5c500701-isolated-workers-arm64.md)
compares integrated rendering, disposable gVisor workers, and warm workers under gVisor and runc.
It records startup, sequential latency, and available CPU/memory measurements with explicit limits;
it does not establish concurrent production capacity. See the [worker experiment](../docs/isolated-workers.md)
for the decision, what the results change, and how to reproduce them from the last commit that has
the code. The
[isolation follow-up](results/2026-10-02-b88e5b5-isolation-followup-arm64.md) splits sandbox
startup and teardown from rendering, compares the `vfs` and `overlay2` storage drivers, prices
tagged PDFs, and tests Chromium's own sandbox under runc and gVisor. Its probes were ad hoc and
exploratory; the worker experiment records how they change the design.

The [Azure Container Apps Sandboxes run](results/2026-10-03-5b667b4-azure-sandboxes-amd64.md)
runs the server image as a per-customer renderer in Azure's microVM sandboxes: Chromium's sandbox,
warm and cold conversion times, suspend and resume, snapshots, egress, and smaller sizes.
[`azure-sandboxes/run.py`](azure-sandboxes/run.py) reproduces it in a resource group of its own,
which it deletes afterwards; it needs an Azure subscription and the `aca` CLI. The
[hosted renderer design](../docs/hosted-renderers.md#azure-container-apps-sandboxes) records what it
changes. The [follow-up](results/2026-10-04-5d557b4-azure-sandboxes-followup-amd64.md) tests port
authentication, a resume-only role, DNS, concurrent load, and on-demand activation, with
[`followup.py`](azure-sandboxes/followup.py) and the probes beside it. The
[end-to-end run](results/2026-10-04-b9c51da-hosted-renderers-e2e-amd64.md) puts the provisioner
and the gateway together on real sandboxes: waking, the allow-list, a rollout under load, the kill
switch, and deleting a tenant, with [`e2e.py`](azure-sandboxes/e2e.py). The
[production-shaped run](results/2026-10-04-6cdce25-hosted-renderers-production-amd64.md) deploys
the gateway as a Container App behind a NAT gateway, with Key Vault records, a managed identity,
and renderers in a virtual network without DNS, and measures hardening, creates, wakes, warm
latency, 15 minutes of load with one and two gateway replicas, and a rollout under load, with
[`production.py`](azure-sandboxes/production.py). The
[workspaces run](results/2026-10-04-f03e90e-hosted-renderers-workspaces-amd64.md) adds the
provisioning service, which creates a renderer on each workspace's first conversion, for two
applications with tenant prefixes: isolation, a burst of new workspaces, quotas, fairness under a
flood of new tenant IDs, steady use, deletes, the kill switch, retirement, a rollout that retires
stopped renderers, and leftover cleanup, with [`workspaces.py`](azure-sandboxes/workspaces.py) and
[`workspace-client.py`](azure-sandboxes/workspace-client.py).

The current `e2e.py`, `production.py`, and `workspaces.py` runners use OnDemand renderer ports.
The gateway identity needs no sandbox read/resume role, and the runners omit the removed
`Gateway:Wake` and `Provisioner:PortActivation` settings. E2E checks concurrent requests waking
one tenant; production measures OnDemand wake after a gateway restart (`vrestart`,
`--restart-cycles`). Use a fresh work directory and results file for these scenarios. Existing
Manual-port renderers must be recreated before using the current gateway; cleanup still
recognizes old run state, role assignments, and custom role definitions.

The linked result files remain historical measurements at their recorded commits. The Manual
activation and resume-role probes in `followup.py` are historical Azure platform experiments,
not configuration required by the current hosted service. Check out the recorded commit when
reproducing an old comparison exactly.

Production and workspaces share [`common.py`](azure-sandboxes/common.py) for private state,
redaction, token caching by actual expiry, networking, and sandbox image setup. Their runtimes
remain independent. Validate these helpers without an Azure account, CLI calls, or network:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s benchmarks/azure-sandboxes -p 'test_*.py' -v
```

## What runs

| Suite | Project | Measures |
|---|---|---|
| Load comparison | [`Atli.Reports.Benchmarks.Load`](Atli.Reports.Benchmarks.Load) + [`load/compose.yaml`](load/compose.yaml) | Throughput, latency percentiles, errors, CPU and memory of `Atli.Reports.Server` and Gotenberg, each in a container with the same limits |
| Micro-benchmarks | [`Atli.Reports.Benchmarks`](Atli.Reports.Benchmarks) (BenchmarkDotNet) | Latency and managed allocations of both `IHtmlToPdfConverter.ConvertAsync` overloads, in-process |

### Fixtures

Every fixture is a self-contained HTML file in [`fixtures/`](fixtures): no network or file fetches
during conversion. Both targets print every fixture on **A4 with 0.4 in margins, background
graphics, and a tagged PDF**.

| Fixture | HTML | Pages | Exercises |
|---|---:|---:|---|
| `invoice` | 8 KB | 1 | Embedded CSS, inline SVG logo, line-item table |
| `long-table` | 446 KB | 49 | A 2,400-row ledger with a repeating header; pagination and a large PDF (7 MB tagged) |
| `chart` | 12 KB | 1 | Charts drawn by JavaScript after an async "data load"; the page signals when it is ready |
| `assets` | 1.6 MB | 2 | 11 PNG images inlined as base64 data URIs, the way Atli.Reports.Blazor inlines report assets |

The readiness contract of `chart` is the same for both targets: the page calls
`window.reportReady()` (Atli: `waitForSignal: "reportReady"`) and sets `window.reportRendered = true`
(Gotenberg: `waitForExpression: "window.reportRendered === true"`) in the same statement, after the
charts are laid out. `long-table.html` and `assets.html` are generated deterministically (fixed seed,
uncompressed PNG blocks, so identical bytes on every platform); regenerate them with
`dotnet run --project benchmarks/Atli.Reports.Benchmarks.Load -- generate-fixtures`.
`invoice.html` and `chart.html` are written by hand.

## Methodology

**One target at a time, fresh for every cell.** A *cell* is one target × fixture × concurrency level.
For each cell the driver starts the target from `load/compose.yaml` (`docker compose up
--force-recreate`), waits for its health endpoint, measures, saves the container log, and removes
the container. Nothing carries over between cells: a crash, a leak, or a browser restart in one cell
cannot affect the next. Cells run fixture → concurrency → target, and the target order alternates
from cell to cell, so drifting background load hits both targets alike.

**Identical limits.** Both services share one YAML anchor: `cpus: 2`, `mem_limit: 2g`,
`memswap_limit: 2g` (no swap, so memory pressure shows up as OOM kills rather than slow paging), and
`restart: "no"` (a crash stays visible). Change them with `--cpus` and `--memory`; they apply to
both.

**Load model.** Closed loop: *c* workers each send a request, wait for the complete response, and
send the next one. Per cell:

1. *Warm-up* — `min(c, 4)` workers for `--warmup` seconds, then in-flight requests finish. This starts
   lazily launched browsers (Gotenberg starts Chromium on the first request) and warms caches
   without queueing a backlog into the measurement. Warm-up results are recorded but not scored.
2. *Measured phase* — all *c* workers start together and keep starting requests for `--duration`
   seconds; then the requests still in flight finish (the *drain*). Every measured request is
   accounted for: it either returns or hits the client deadline.

**Client deadline.** 30 s per request (`--timeout`), equal to Gotenberg's default `--api-timeout`.
A PDF that takes longer is a failure for both targets, whichever side gives up first. After a
connection-level failure a worker pauses 250 ms, so a crashed target is not hammered in a tight loop.

**Duration.** Quick mode is 16 cells and takes about 6 minutes; full mode is 32 cells and takes
about 25 minutes. A cell that saturates its target takes longer, because its failing requests wait
out the client deadline (in the 2026-10-01 run: Gotenberg on `long-table` at concurrency 16, and
Atli on `long-table` at concurrency 64).

**Escalation stops at the breaking point.** When a level produces no valid PDF for a target and
fixture, the higher levels are recorded as *skipped* instead of each waiting out the deadline
(`--keep-going` measures them anyway).

**Same request, same document.** The driver prepares each request body once per fixture and replays
it: JSON for `POST /convert` (Atli), multipart for `POST /forms/chromium/convert/html` (Gotenberg).
Every response is validated: HTTP 2xx, a `%PDF-` header, a `%%EOF` trailer, and a page count (the
largest `/Count` in the page tree). The report's *Output check* compares page counts and PDF sizes
between targets, and *Anomalies* flags any mismatch.

### Metrics

| Metric | Definition |
|---|---|
| docs/s | Valid PDFs from the measured phase ÷ the phase's wall time, drain included. By Little's law this is the steady-state rate even when latencies are long. |
| p50/p90/p95/p99/max | Nearest-rank latency percentiles (send → last response byte) of the successful measured requests. Read them together with *requests*: at low throughput they rest on few samples. |
| errors | Failed measured requests by class: `http_<status>`, `client_timeout`, `transport_<socket error>`, `invalid_pdf`. *Anomalies* quotes the first error body of each class. |
| CPU cores (avg) | Container cgroup `cpu.stat usage_usec` delta over the measured phase. 2.00 = both CPUs busy. |
| CPU s/doc | The same CPU time divided by the valid PDFs: what one document costs. It is the metric least distorted by other load on the machine, because it does not depend on wall-clock time. |
| CPU % (peak) | Highest `docker stats` reading during the measured phase (percent of one core). |
| peak mem | Highest `docker stats` memory reading over the whole cell (cgroup usage minus inactive page cache, sampled at ~1 Hz, so short spikes can be missed). |
| cgroup memory.peak | The kernel's exact peak for the container (cgroup v2), page cache included — so it also counts the browser binary's file cache. |
| OOM kills | `memory.events oom_kill` of the container: processes the kernel killed at the memory limit. |
| Docker load | The Docker host's (on macOS: the Linux VM's) 1-minute load average when the cell started and ended. |

### Fairness rules and knobs

Each side runs with its **shipped defaults**; the only per-request settings are those needed to
print the same document.

| | Atli.Reports.Server | Gotenberg |
|---|---|---|
| Image | Built from [`src/Atli.Reports.Server/Dockerfile`](../src/Atli.Reports.Server/Dockerfile) as `atli-reports-server:bench`: the NativeAOT server on Ubuntu 24.04 with `chrome-headless-shell` from Chrome for Testing (the version the Dockerfile pins, unless the `CHROME_VERSION` build argument overrides it) | `gotenberg/gotenberg:8.37.0-chromium`, pinned by multi-arch digest (Chromium only, no LibreOffice) |
| Server settings | The image's defaults: [`appsettings.json`](../src/Atli.Reports.Server/appsettings.json) sets `WarmUpOnStartup`, `Headless`, `DisableDevShmUsage`, a 30 s `CommandTimeout`, a 60 s `ConversionTimeout`, and a queue of 100 with a 30 s `QueueTimeout`; the Dockerfile sets `Browser:ExecutablePath`. Chromium's sandbox is on, under the Compose file's `seccomp` profile ([`deploy/seccomp`](../deploy/seccomp/README.md)); results recorded before that change ran with `NoSandbox`. `MaxConcurrentConversions` keeps its default, the processor count clamped to 2–8 (2 under `cpus: 2`). No `ReportsEngine__*` overrides | Defaults: `--chromium-max-concurrency=6`, `--chromium-max-queue-size=0` (unbounded), `--chromium-restart-after=100`, `--chromium-auto-start=false`, `--api-timeout=30s` |
| Logging | Default (`Information`) | Default (`info`, one access line per request) |
| Per request | `paperSize: a4`, margins 0.4, `printBackground: true`; `waitForSignal` for `chart` | `paperWidth=8.27`, `paperHeight=11.69`, margins 0.4, `printBackground=true`, `generateTaggedPdf=true`; `waitForExpression` for `chart` |

Why `generateTaggedPdf=true`: Atli's Chromium prints tagged (accessible) PDFs by default, while
Gotenberg launches Chromium with `--disable-pdf-tagging` and only tags when asked. Tagging is not
free — on `long-table` it makes the PDF 13× larger (7.2 MB vs 0.55 MB) and the conversion ~1.6×
slower — so comparing Atli's tagged output with Gotenberg's untagged output would not compare like
with like. To compare untagged output instead, apply it to **both** sides: set
`ReportsEngine__Browser__ExtraArguments__0=--disable-pdf-tagging` in `load/atli.env` and change the
Gotenberg field in [`BenchmarkTarget.cs`](Atli.Reports.Benchmarks.Load/Targets/BenchmarkTarget.cs).

Tuning either side is possible without editing tracked files: `load/atli.env` (git-ignored) feeds
`ReportsEngine__*` variables to the Atli container, and `GOTENBERG_ARGS` adds Gotenberg flags. The
results record the container environment and command line, so a tuned run is never mistaken for a
default one. Only compare runs that used the same knobs.

**Why a .NET load driver instead of k6.** The driver ([`Atli.Reports.Benchmarks.Load`](Atli.Reports.Benchmarks.Load))
builds with the solution and needs nothing beyond the SDK. It validates *every* PDF (k6 would need
JavaScript PDF parsing per response), classifies errors by type, computes exact percentiles from
all samples, samples the container through the Docker CLI and cgroup files, and orchestrates the
fresh-container-per-cell sequence in one place. It runs on the host, outside the containers' CPU
quota, and both targets are reached through the same port-forwarding path.

## Results

Each run writes to [`results/`](results):

- `<yyyy-mm-dd>-<short-sha>.md` — summary per fixture, output check, anomalies, per-cell detail,
  environment (host CPU/OS, load averages at start and end, Docker resources, other containers
  running, image IDs, browser versions, git and engine commits), and the knobs in effect
- `<yyyy-mm-dd>-<short-sha>.json` and `.csv` — the raw numbers (JSON has every field; CSV is one row
  per cell)
- `<yyyy-mm-dd>-<short-sha>-micro.md` — the BenchmarkDotNet table, when `--micro` ran
- `<name>.work/` (git-ignored) — per-request samples and container logs of every cell, for digging
  into anomalies

`dotnet run --project benchmarks/Atli.Reports.Benchmarks.Load -- report <file>.json` re-renders the
Markdown and CSV from the JSON.

### Rerunning for a comparison

```bash
git pull
benchmarks/run.sh --full --micro --note "Quiet machine, after <change>"
```

Then compare the new file with the committed baseline. For numbers worth publishing:

- run on a quiet machine — the report's *Environment* section lists the host load averages and every
  other running container, so check them before trusting a result;
- keep Docker's resources unchanged between the runs you compare (the report records them);
- compare the same mode, limits, and knobs; and
- treat cells with few requests (slow fixtures at concurrency 1) as indicative.

The micro-benchmarks use the browser installed on the host, so they are only comparable on the same
machine.

## Caveats

- On macOS, Docker runs in a Linux VM; the containers' CPU quota applies inside that VM, and
  networking crosses the VM boundary. Absolute numbers differ from a Linux server; the comparison is
  what matters, since both targets take the same path.
- The two images ship different browser builds: Atli's ships `chrome-headless-shell` from Chrome for
  Testing, Gotenberg's ships Debian's Chromium. The report records both image IDs and, where it
  can read it, the browser version.
- `docker stats` samples about once a second; the cgroup `memory.peak` is exact but includes page
  cache.
- The micro-benchmarks' *Allocated* column counts the benchmark process's managed allocations only,
  not the browser's memory.
