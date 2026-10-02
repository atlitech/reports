# Isolated worker exploratory measurements — ARM64, 2026-10-02

The run completed 96 conversions with no errors: four fixtures × four modes × one initial sample plus five measured samples. All modes produced the same expected page counts (invoice 1, assets 2, long table 49, chart 1) and matching PDF byte counts for each fixture. Every worker response passed framing and PDF trailer checks; each worker process completed with clean EOF and exit code 0. The full containment and HTTP lifecycle smoke also passed before timing.

Source: `5c500701aee0788efcb7ce052bb53f667fefa297`, clean working tree. [Machine-readable evidence](2026-10-02-5c500701-isolated-workers-arm64.json) includes image IDs, verified runtime checksum, limits, startup samples, throughput, and available cgroup measurements.

## Conditions and limits

- ARM64 OrbStack Linux VM, Docker 29.4.0, 10 available virtual CPUs and about 15.7 GiB VM memory. Each measured report process/container had a 1 CPU, 1 GiB memory, 256 PID, and 512 MiB temporary-filesystem limit. The integrated server had one conversion slot.
- gVisor `release-20260928.0`, systrap platform, running under pinned Docker 29.8.2 DinD. The nested daemon used the **vfs** storage driver. Images were already cached; image download and compilation are excluded.
- Fixed order: integrated, disposable gVisor, warm gVisor, warm runc; within each mode: invoice, assets, long table, chart. Shared host caches and order effects were not controlled. This is one exploratory run, not a capacity, concurrent-load, cost, or production-cloud benchmark.
- Integrated requests use HTTP to a NativeAOT server. Worker requests use Python framing over nested Docker CLI stdio. The warm runc control uses the same image, transport, resource limits, and fixtures as warm gVisor; runc is only a benchmark control and makes no stronger isolation claim.
- Warm workers reuse one browser sequentially in one trust domain, with a fresh browser context per conversion. This is a benchmark-only comparison; the experimental gateway still launches a new sandbox per request. Each disposable sample includes startup and cleanup.
- The first request per fixture is recorded separately. Only the first fixture starts a warm worker; later first requests are warmups. Five-sample p95 is the maximum observed sample and is too small for a latency SLO.

## Latency and sequential rate

| Execution | Fixture | p50 (s) | p95 (s) | Reports/s |
|---|---|---:|---:|---:|
| Integrated HTTP, warm | invoice | 0.081 | 0.091 | 16.490 |
| Integrated HTTP, warm | assets | 0.165 | 0.179 | 6.427 |
| Integrated HTTP, warm | long-table | 1.569 | 1.657 | 0.637 |
| Integrated HTTP, warm | chart | 0.062 | 0.082 | 15.829 |
| gVisor, disposable | invoice | 2.246 | 3.815 | 0.397 |
| gVisor, disposable | assets | 2.497 | 3.049 | 0.386 |
| gVisor, disposable | long-table | 16.629 | 18.363 | 0.064 |
| gVisor, disposable | chart | 6.194 | 6.867 | 0.181 |
| gVisor, warm sequential | invoice | 0.294 | 0.316 | 3.582 |
| gVisor, warm sequential | assets | 0.567 | 0.675 | 1.712 |
| gVisor, warm sequential | long-table | 11.356 | 12.216 | 0.089 |
| gVisor, warm sequential | chart | 0.598 | 0.745 | 1.676 |
| runc, warm sequential control | invoice | 0.133 | 0.157 | 8.495 |
| runc, warm sequential control | assets | 0.276 | 0.305 | 3.620 |
| runc, warm sequential control | long-table | 3.037 | 3.126 | 0.346 |
| runc, warm sequential control | chart | 0.078 | 0.085 | 12.940 |

## Observed CPU and memory

| Warm worker, 24 reports including warmups | CPU time (s) | Cumulative cgroup memory peak (MiB) |
|---|---:|---:|
| gVisor, warm sequential | 66.14 | 805.4 |
| runc, warm sequential control | 18.03 | 864.9 |

These are trusted outer-daemon cgroup v2 observations, not the guest’s virtual `/proc`. gVisor values include the sandbox and gofer. CPU samples are taken at fixture boundaries and omit the final teardown; memory is a cumulative cgroup peak, including charged memory beyond process RSS, across the entire fixture sequence. They exclude the outer Docker daemon, CLI, gateway, and VM. Integrated and disposable CPU/memory measurements are unavailable and remain null; zero was not substituted.

## Implication for the next design iteration

Keeping a browser warm substantially reduces startup cost for small reports in this environment. The gVisor warm control still shows material runtime overhead compared with the equivalent runc control, especially for the 49-page report and the JavaScript chart. A bounded warm pool per trust domain is worth measuring next; this result does not justify sharing a sandbox across hostile tenants. Validate longer runs, concurrent load, randomized fixture order, and the intended production runtime/storage before choosing an SLO or estimating hosting cost.

## Reproduce

Build the server and worker images using their repository Dockerfiles, build the Debug server DLL, then run:

```sh
python3 -B .github/scripts/validate-isolated-workers.py --samples 5 \
  --worker-image atli-reports-worker:security \
  --server-image atli-reports-server:security \
  --gateway-dll artifacts/bin/Atli.Reports.Server/debug/Atli.Reports.Server.dll \
  --output artifacts/isolated-worker-results.json
```

The script downloads and verifies the pinned official gVisor bundle (or verifies `--bundle PATH`), creates a uniquely named privileged DinD test daemon without host mounts or a published Docker endpoint, and removes only its own containers and volumes. This local privileged test control plane is trusted infrastructure, not a production deployment example.
