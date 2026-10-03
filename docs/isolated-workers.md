# Isolated renderer experiment

Status: research, closed; the code is removed. The experiment merged in #152 had the public API
launch a fresh gVisor container for every conversion. It priced the strongest boundary available
to it. It is not the production architecture. The default server remains the integrated engine for
self-hosted, application-owned reports. This page records the decision and the measurements behind
it. The [hosted renderer design](hosted-renderers.md) replaces the experiment's shape. The code is
at commit [`6f30bff`](https://github.com/atlitech/reports/tree/6f30bffc354394d1866bc68b9f4733f8c3e9cdcd),
the last one that has it.

## Status and decision

In the experiment, the API process ran `docker run --runtime=runsc` once per request
([`WorkerLauncher.cs`](https://github.com/atlitech/reports/blob/6f30bffc354394d1866bc68b9f4733f8c3e9cdcd/src/Atli.Reports.Server/Execution/WorkerLauncher.cs)).
That shape is wrong for production in two ways, and the self-hosted product does not need it:

- **Wrong layer.** Launching containers requires authority over a Docker daemon, which is
  root-equivalent on its host. Compromising the API compromises the daemon's workload domain, so
  the most exposed component holds the most dangerous privilege. Neither Azure Container Apps nor
  Kubernetes gives a container a Docker daemon.
- **Wrong unit.** One sandbox per job is stricter than needed. The boundary that matters in a
  hosted service is between customers. Per-job sandboxes pay sandbox start, browser start, and
  teardown on every request; for one-page reports that dominated the measured time.
- **Not needed for self-hosting.** The [security model](security.md) covers application-owned
  templates and controlled assets, which the integrated engine serves.

Decisions:

1. Self-hosted deployments use the integrated engine; it is the supported mode. The server image
   runs Chromium's sandbox by default. It needs the seccomp profile
   [`deploy/seccomp/chromium.json`](../deploy/seccomp/README.md) and fails closed without it; see
   [Chromium's sandbox](security.md#chromiums-sandbox). Operators who want defense in depth can
   run the same image under a sandboxed runtime, such as a gVisor or Kata RuntimeClass. Under
   gVisor on arm64, Chromium's sandbox crashed in the follow-up probes and the browser then hung
   instead of exiting at startup, which the engine does not report as a sandbox failure. Operators
   there must opt out explicitly (`ReportsEngine__Browser__NoSandbox=true`) and trade one layer for
   the other. The probes used the worker image; the server image under gVisor, and amd64, were not
   tested.
2. A managed service, if one is built, puts a shared API in front of per-customer renderer
   deployments. See the [hosted renderer design](hosted-renderers.md).
3. Remove the experiment's code: the API's worker mode and Docker launcher, the worker, its private
   protocol, their tests, the benchmark harness, and its workflow. The hosted design calls the
   server image's `POST /convert` and uses none of it. See
   [What remains of #152](#what-remains-of-152).

## What the measurements show

### Initial measured outcome

The [ARM64 exploratory run](../benchmarks/results/2026-10-02-5c500701-isolated-workers-arm64.md)
completed all 96 conversions. With one CPU and 1 GiB per container, median time for the 49-page
report was 1.57 seconds in the integrated engine, 16.63 seconds in a disposable gVisor worker,
11.36 seconds in a warm gVisor worker, and 3.04 seconds in the equivalent warm runc control.
The warm gVisor worker consumed 66.14 CPU seconds across 24 reports including warmups, compared
with 18.03 seconds for the runc control; those observations exclude final teardown and the gateway.
That run predates the server image's sandbox: its integrated engine ran Chromium with
`--no-sandbox`.

These results favor retaining the integrated default and treating runtime selection as an open
performance decision. Browser reuse reduces startup cost, but it does not eliminate the observed
gVisor overhead. The local nested daemon uses `vfs`, transports differ from the integrated API, and
each fixture has only five measured samples; the reported p95 is a maximum observation, not a
production SLO.

### Follow-up findings

The [follow-up probes](../benchmarks/results/2026-10-02-b88e5b5-isolation-followup-arm64.md) ran
on the same ARM64 OrbStack VM with the same image IDs and per-container limits. An ad hoc,
uncommitted client drove the private protocol directly. Values are medians of four or five samples
after one warmup unless the results file says otherwise. They are exploratory, and no CPU, memory,
or p95 figures were measured.

- **Follow-up probes measured much lower warm gVisor times; the gap is unexplained.** A fresh warm
  gVisor worker per fixture on `overlay2` took 4.37 s for the tagged 49-page report (recorded:
  11.36 s), 0.21 s for the chart (0.60 s), and 0.21 s for the invoice (0.29 s). The recorded run
  used `vfs` and ran all four fixtures in one worker, whose cgroup memory peak reached about
  805 MiB of its 1 GiB limit. Later runs on this host drifted by amounts of the same order (see the
  render-time bullet below). The storage driver, sequential reuse near the memory limit, and drift
  are all candidates; none was isolated.
- **Startup and teardown dominate single-use workers.** An empty gVisor sandbox's full lifecycle
  took 0.74 s on `vfs` and 0.26 s on `overlay2`. A single-use gVisor worker finished the invoice
  PDF 1.50 s (`vfs`) or 1.06 s (`overlay2`) after `docker run` started, and its container was gone
  0.29 s or 0.18 s later. A single-use runc worker finished in 0.26 s and was gone at 0.34 s. The
  recorded disposable gVisor p50 was 2.25 s on `vfs`, including the harness's cleanup commands;
  the gateway runs the same `docker rm --force` before it responds.
- **Teardown sits on the response path.** The gateway waits for the worker to exit, which follows
  browser shutdown (a process-tree kill, then profile cleanup), and removes the container before
  `ConvertAsync` returns. The endpoint completes the HTTP response only after that, so the teardown
  tail is client-visible latency and holds a job slot.
- **The storage driver matters.** The harness's nested daemon uses `vfs`, which added about 0.5 s
  to every sandbox lifecycle compared with `overlay2`. Measure on the storage driver production
  would use.
- **Tagged PDFs cost time on long reports.** Under runc, tagging made the 49-page report 7.19 MB
  instead of 0.54 MB and took 1.899 s instead of 1.076 s. Under gVisor it took 4.37 s instead of
  3.36 s. One-page fixtures showed no material difference. The harness always tagged, the load
  benchmark always tags, and the integrated engine tags unless a request sets `generateTaggedPdf`
  to false.
- **gVisor still costs render time on long reports.** In one follow-up run, the 49-page report took
  2.3 times as long under gVisor as under runc tagged, and 3.1 times untagged. The runc probes used
  the host daemon and the gVisor probes a nested one. A later run of the same untagged gVisor
  configuration took 5.04 s instead of 3.36 s, and the next rounds, with more CPU or more memory,
  took 7.67 s and 9.49 s. This laptop VM can neither pin down the ratio nor size CPU or memory
  budgets.
- **The browser process model is not the lever.** Under gVisor, reduced process models
  (`--no-zygote`, `--single-process`, one renderer process without site isolation) shortened a
  cold one-page command-line print from 1.79 s to as little as 1.34 s. They did not materially
  change the long report (7.25 to 7.84 s), and they weaken Chromium's own isolation.
- **Chromium's sandbox is cheap under runc.** Docker's default seccomp profile makes Chrome abort
  with "No usable sandbox!". At the probed revision the server image disabled Chromium's sandbox
  for container compatibility; the worker image relies on the outer runtime instead. With a probe
  profile that allowed `clone`, `unshare`, and `chroot` on top of Docker's default, without
  argument filters, Chrome started with its sandbox and the worker converted every fixture, with
  all other container hardening kept. Five interleaved rounds showed about 30 ms extra at cold
  start and warm differences within noise in both directions. The server image now runs the
  sandbox under [`deploy/seccomp/chromium.json`](../deploy/seccomp/README.md), which limits `clone`
  to user, PID, and network namespaces and `unshare` to a user namespace alone. OrbStack's kernel
  permits unprivileged user namespaces; hosts that restrict them through AppArmor, such as Ubuntu
  23.10 and later, were not tested.
- **Chromium's sandbox crashes inside gVisor on arm64.** Chrome's own seccomp-bpf SIGSYS
  handler crashes on arm64 syscall 123 (`sched_getaffinity`), and the process hangs until killed.
  The worker image's `ATLI_WORKER_NO_SANDBOX=true` was therefore required under gVisor on arm64,
  not merely convenient. amd64 was not tested.
- **gVisor's KVM platform was not tested.** OrbStack exposes no `/dev/kvm`; every gVisor number
  here uses systrap.

### Reading the results now

The recorded run's conclusion stands: keep the integrated default. Three parts of its reading
change. First, the recorded gVisor lifecycle and render figures were higher than the follow-up
probes on the same host, but later runs there drifted by amounts of the same order, so neither
set is a stable cost. The warm long-report ratio to runc was 3.7 in the recorded run and 2.3
(tagged, runc on the host daemon, gVisor nested) in one follow-up run. Second, most of the per-job
cost for small reports is sandbox and browser lifecycle, storage driver, and teardown, not
rendering. Warm renderers remove it; a faster per-job launcher would only shrink it. Third, a
gVisor render overhead on long reports appears in every run, but its size is not stable on a
laptop VM and must be measured on the intended platform.

## What remains of #152

The [hosted design](hosted-renderers.md) reaches renderers over HTTPS `POST /convert`, so it uses
none of the experiment's code, and that code was removed: the API's worker mode
(`ReportsServer:Execution`), the Docker launcher, the worker, its private protocol, their tests,
the benchmark harness, and its workflow. No release shipped them. What remains:

- This page, the [recorded run](../benchmarks/results/2026-10-02-5c500701-isolated-workers-arm64.md),
  and the [follow-up probes](../benchmarks/results/2026-10-02-b88e5b5-isolation-followup-arm64.md).
- The code at commit [`6f30bff`](https://github.com/atlitech/reports/tree/6f30bffc354394d1866bc68b9f4733f8c3e9cdcd),
  for reproducing the measurements (see [The experiment as built](#the-experiment-as-built)). The
  gateway's response checks there are the model for the API's renderer-response checks (see
  [API-to-renderer transport](hosted-renderers.md#api-to-renderer-transport)).

## The experiment as built

This page at commit `6f30bff` describes the code as it was built:
[its trust boundary](https://github.com/atlitech/reports/blob/6f30bffc354394d1866bc68b9f4733f8c3e9cdcd/docs/isolated-workers.md#trust-boundary),
the [execution settings](https://github.com/atlitech/reports/blob/6f30bffc354394d1866bc68b9f4733f8c3e9cdcd/docs/isolated-workers.md#selecting-the-execution-backend),
the [measurement plan](https://github.com/atlitech/reports/blob/6f30bffc354394d1866bc68b9f4733f8c3e9cdcd/docs/isolated-workers.md#measurement-plan),
and [how to reproduce the experiment](https://github.com/atlitech/reports/blob/6f30bffc354394d1866bc68b9f4733f8c3e9cdcd/docs/isolated-workers.md#reproducing-the-experiment).

In short: check out that commit, build the server and worker images, and run
`.github/scripts/validate-isolated-workers.py`. It starts a privileged Docker-in-Docker daemon with
a checksum-verified gVisor bundle as trusted test infrastructure. Its nested daemon uses the `vfs`
storage driver, which lengthens every sandbox lifecycle, and it requests tagged PDFs, which
lengthen long reports; see the [follow-up findings](#follow-up-findings).
