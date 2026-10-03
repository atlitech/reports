# Isolation follow-up exploratory measurements — ARM64, 2026-10-02

These probes followed the
[recorded isolated-worker run](2026-10-02-5c500701-isolated-workers-arm64.md). They separate
sandbox startup and teardown from rendering, compare Docker storage drivers, price tagged PDFs, try
reduced Chromium process models, and test Chromium's own sandbox under runc and gVisor. The
[isolated renderer experiment](../../docs/isolated-workers.md) records what they change in the
design.

This is exploratory evidence. The probe scripts were ad hoc and are not committed; no JSON file
accompanies this summary. Samples are small, the host is a laptop VM, and no p95, throughput, CPU,
or memory figures were measured. Every follow-up value below is a wall-clock median observed by
the probe client. Where a design statement needs memory, it cites the recorded run instead.

Source: repository at `b88e5b5`. The images were built from `5c500701`, the revision of the
recorded run; `git diff 5c500701 b88e5b5` changes only documentation and result files, so the
worker, protocol, engine, and server sources are identical.

## Conditions and limits

- ARM64 OrbStack Linux VM on Apple Silicon: Docker 29.4.0, kernel
  `7.0.14-orbstack-00380-ga7e0a2dc9535`, `aarch64`, 10 virtual CPUs, about 15.7 GiB VM memory. This
  is the host of the recorded run.
- Worker image `sha256:a72bb52ee83c53d70538a968f429a1b67c996c23ebf80fef6123a39c6f33ab34` and
  server image `sha256:65b54f9ca7647e7be123bc033facd6b5ebe3a49b74a216e7205010692e91921b`, the same
  image IDs as the recorded run. Chrome for Testing 154.0.8037.92.
- Every worker container ran with the launcher's limits: `--cpus=1`, `--memory=1073741824` with
  equal `--memory-swap` (no swap), `--pids-limit=256`, `--read-only`, a 512 MiB `/tmp` tmpfs,
  `--network=none`, `--cap-drop=ALL`, `no-new-privileges`, user 1654, and `--init`. Section 4
  varies CPU and memory; section 6 changes only the seccomp profile.
- runc probes ran on the host's Docker daemon (`overlay2`).
- gVisor probes used `release-20260928.0` on the systrap platform (`runtimeArgs`
  `--platform=systrap --network=none`) inside a nested `docker:29.8.2-dind` daemon, as the
  committed harness does. That daemon used either `vfs`, the harness default, or `overlay2` on an
  anonymous volume at `/var/lib/docker`. Each table names the storage driver.
- An ad hoc Python client drove the private stdio protocol directly: no HTTP gateway, no API
  authentication, no `WorkerConverter`. Single-use timings start when the client starts
  `docker run`; warm timings cover one job, from its request to the end of its PDF stream.
- Fixtures come from [`benchmarks/fixtures`](../fixtures): A4 paper, 0.4 in margins,
  `printBackground`, and `waitForSignal: "reportReady"` for `chart`. Each table states
  `generateTaggedPdf`, or says that it was not recorded.
- Unless a table says otherwise, a value is the median of four or five samples after one warmup.
  Values are rounded; derived columns need not subtract exactly. Sizes are in MB (10^6 bytes).

## 1. Tagged PDF cost (warm worker)

The committed harness sets `generateTaggedPdf: true` (`options()` in
[`validate-isolated-workers.py`](https://github.com/atlitech/reports/blob/b88e5b5da076f1aa3d27cc98808032c95d52ff6d/.github/scripts/validate-isolated-workers.py)), and the
[load benchmark](../README.md#fixtures) prints every fixture tagged. Without the option, the
integrated engine leaves the choice to Chromium, which tags by default.

| Runtime | Fixture | Tagged (s) | Untagged (s) | Tagged (MB) | Untagged (MB) |
|---|---|---:|---:|---:|---:|
| runc, host `overlay2` | long-table | 1.899 | 1.076 | 7.19 | 0.54 |
| runc, host `overlay2` | chart | 0.062 | 0.056 | 0.06 | 0.04 |
| runc, host `overlay2` | invoice | 0.072 | 0.073 | 0.07 | 0.04 |
| gVisor, nested `overlay2` | long-table | 4.37 | 3.36 | 7.19 | 0.54 |
| gVisor, nested `overlay2` | chart | 0.21 | 0.21 | not recorded | not recorded |

Tagging made the 49-page report about 13 times larger and 1.8 times slower under runc, and
1.3 times slower under gVisor. It made no material time difference for the one-page fixtures.

## 2. Startup and teardown

An empty gVisor sandbox (`docker run --rm --runtime=runsc ... --entrypoint /bin/true`), timed for
the whole command, median of four:

| Nested daemon storage | Empty sandbox lifecycle (s) |
|---|---:|
| `vfs` | 0.74 |
| `overlay2` | 0.26 |

A single-use worker converting `invoice` once; tagging was not recorded. *PDF complete* is the end
of the PDF stream; *gone* is the point at which the container no longer existed. The tail is the
time between them. The runc tail was not recorded separately; the table gives the difference
of its two medians.

| Runtime and storage | PDF complete (s) | Gone (s) | Tail (s) |
|---|---:|---:|---:|
| gVisor, nested `vfs` | 1.50 | 1.79 | 0.29 |
| gVisor, nested `overlay2` | 1.06 | 1.25 | 0.18 |
| runc, host `overlay2` | 0.26 | 0.34 | about 0.08 |

The recorded run's disposable gVisor `invoice` p50 was 2.246 s on `vfs`. Its timing also covers
the harness's own cleanup: `docker rm --force` and, when that finds no container, a `docker ps`
check, each issued through `docker exec` into the test daemon. The gateway also runs
`docker rm --force` before it responds, so that cleanup belongs in a client-visible figure. The
follow-up did not use the harness, and the cause of the remaining difference was not isolated.

On the gateway path (at `b88e5b5`), teardown precedes the end of the HTTP response.
`WorkerConverter.ConvertAsync` awaits its reader, which requires the worker's end of output, and
then the process exit (`src/Atli.Reports.Server/Execution/WorkerConverter.cs`, line 170). Its
`finally` block then calls `CleanupAsync` (line 224), which runs `docker rm --force` (lines
401–429) before `ConvertAsync` returns. The `/convert` endpoint completes the response only after
that call returns (`src/Atli.Reports.Server/Endpoints/ConvertEndpoints.cs`, line 144). After its
last job, the worker stops its hosted services before it exits
(`src/Atli.Reports.Worker/Program.cs`, lines 35–42). They drain, close the DevTools connection,
kill the browser's whole process tree, wait for it to exit, and delete the profile directory,
rather than asking the browser to close. In the gateway, a tail like the one above is
therefore client-visible latency, and the job slot stays occupied until cleanup completes. The
gateway path itself was not timed.

## 3. Warm gVisor: follow-up run against the recorded run

A fresh warm worker per fixture on nested `overlay2`, against the recorded warm gVisor p50 (one
worker for all fixtures in sequence, nested `vfs`, all tagged):

| Fixture | Follow-up (s) | Recorded p50 (s) |
|---|---:|---:|
| long-table, tagged | 4.37 | 11.356 |
| chart | 0.21 | 0.598 |
| invoice, tagging not recorded | 0.21 | 0.294 |

The chart measured 0.21 s tagged and untagged. The recorded run differed in two ways. It used
`vfs`, and one warm worker ran invoice, assets, long-table, and chart in that order. That worker's
cumulative cgroup memory peak reached 844,541,952 bytes (about 805 MiB) of its 1 GiB limit by the
end of the long table, before the chart ran. Both runs used the same laptop VM, where later runs
drifted by amounts of the same order (section 4). The storage driver,
sequential reuse near the memory limit, and drift are all candidates; none was isolated, and this
comparison does not show that either run is the representative one.

## 4. Repeat-run noise (gVisor, nested `overlay2`)

The untagged long table under gVisor, in the order the rounds ran:

| Round | Limits | Median (s) |
|---|---|---:|
| Section 1 run | 1 CPU, 1 GiB | 3.36 |
| Later run, round 1 | 1 CPU, 1 GiB | 5.04 |
| Later run, round 2 | 2 CPU, 1 GiB | 7.67 |
| Later run, round 3 | 1 CPU, 2 GiB | 9.49 |

Each later round was slower than the one before, including the round with more CPU. The result is
inconclusive about CPU and memory budgets. The laptop VM is not a stable host for sizing them.

## 5. Chromium process model under gVisor

A cold command-line print: `chrome-headless-shell --print-to-pdf` in a fresh gVisor container,
nested `overlay2`, timed for the whole container including sandbox and browser start. Median of
three samples; tagging was not recorded. This path bypasses the worker protocol, so compare rows
with each other, not with other sections.

| Added process-model flags | invoice (s) | long-table (s) |
|---|---:|---:|
| None | 1.79 | 7.72 |
| `--no-zygote` | 1.59 | 7.84 |
| `--single-process --no-zygote` | 1.34 | 7.63 |
| `--no-zygote --disable-site-isolation-trials --renderer-process-limit=1 --disable-features=IsolateOrigins,site-per-process` | 1.37 | 7.25 |

Fewer browser processes shortened the small document's cold start by up to 0.45 s. The process
model had no material effect on the long report. The reduced models weaken Chromium's internal
process isolation; they were measured only to locate cost.

## 6. Chromium's sandbox under runc

The worker image on the host daemon, with every container setting above kept, Chromium started
without `--no-sandbox`:

| Seccomp profile | Result |
|---|---|
| Docker's default | Chrome aborts: `FATAL:content/browser/zygote_host/zygote_host_impl_linux.cc:129] No usable sandbox!` |
| `unconfined` (test only) | Works |
| Docker's default plus one rule allowing `clone`, `unshare`, and `chroot` | Works; the worker converts all fixtures |

The full message continues: "If you are running on Ubuntu 23.10+ or another Linux distro that has
disabled unprivileged user namespaces with AppArmor, see
https://chromium.googlesource.com/chromium/src/+/main/docs/security/apparmor-userns-restrictions.md
...". Docker's default profile blocks the sandbox. At `b88e5b5` the server image disabled
Chromium's sandbox for container compatibility; the worker image relies on the outer runtime
instead (and, under gVisor on arm64, has to; see section 7). The base profile was
`seccomp/default.json` from the `main` branch of `moby/profiles`, fetched on 2026-10-02 and not
pinned. The added rule was `{"names": ["clone", "unshare", "chroot"], "action": "SCMP_ACT_ALLOW"}`,
without argument filters. Whether that set is minimal was not tested in these probes. This is the
probe's profile, not [`deploy/seccomp/chromium.json`](../../deploy/seccomp/README.md), the profile
the server image's sandbox needs. That one pins Docker's default to a moby/profiles release and
limits `clone` and `unshare` by their flags; its README records how each rule was found necessary.

Cost of the sandbox, five interleaved rounds, untagged:

| Case | Sandbox off (s) | Sandbox on (s) |
|---|---:|---:|
| Cold single-use invoice | 0.376 | 0.405 |
| Warm invoice | 0.076 | 0.024 |
| Warm chart | 0.052 | 0.090 |
| Warm long-table | 1.139 | 1.115 |

The sandbox showed no measurable cost beyond about 30 ms at cold start. Warm differences are within
noise in both directions. The probe notes do not record whether the cold value ends at *PDF
complete* or at *gone*, and this series was untagged and run separately, so do not compare it with
section 2.

OrbStack's kernel permits unprivileged user namespaces. Ubuntu 23.10 and later, including 24.04,
restrict them through AppArmor (`kernel.apparmor_restrict_unprivileged_userns`) for processes
AppArmor does not confine; whether a container is affected depends on its runtime's AppArmor
profile (see [Chromium's sandbox](../../docs/security.md#chromiums-sandbox)). No such host was
tested.

## 7. Chromium's sandbox under gVisor

Same gVisor release, systrap platform, arm64, Chromium's sandbox on: Chrome's own seccomp-bpf
SIGSYS handler crashes with `seccomp-bpf failure in syscall nr=0x7b arg1=0x7b` (arm64 syscall 123
is `sched_getaffinity`), and the process then hangs until it is killed. With `--no-sandbox` the
worker converts normally. amd64 was not tested. On arm64, the worker image's
`ATLI_WORKER_NO_SANDBOX=true` is therefore currently required under gVisor, not merely convenient.

## Not measured

- gVisor's KVM platform: OrbStack exposes no `/dev/kvm`.
- Any amd64 host, Chromium's sandbox under gVisor on amd64, and AppArmor-restricted hosts.
- MicroVM runtimes such as Kata or Firecracker.
- Concurrent load, CPU time, memory, and per-phase timings inside the worker (sandbox start,
  browser launch, load, print, stream).
- The HTTP gateway path end to end.

## Caveats

- runc probes used the host daemon and gVisor probes a nested daemon. Cross-runtime ratios include
  that difference as well as the runtime.
- Section 4 shows drift between runs of the same configuration larger than several of the effects
  above. Treat single comparisons as indicative and prefer interleaved rounds, as in section 6.
- A one-CPU, 1 GiB worker with sequential requests says nothing about concurrent capacity.

## Method

1. Build the server and worker images from their repository Dockerfiles, as in the
   [reproduction steps](../../docs/isolated-workers.md#reproducing-the-experiment), and compare
   the image IDs above.
2. For gVisor, start a privileged `docker:29.8.2-dind` container and register the
   checksum-verified gVisor bundle as `validate-isolated-workers.py` does (`Lab.prepare`). For
   `overlay2`, set `storage-driver` to `overlay2` and give the container an anonymous volume at
   `/var/lib/docker`. Load the worker image through `docker save` piped into `docker load`.
3. Start a worker with `docker run --rm --interactive`, the limits above, and `--runtime=runsc` or
   the default runc. Set `ATLI_WORKER_MAX_JOBS` to the number of jobs for a warm worker; send one
   job and close standard input for a single-use worker.
4. Write each request as the harness's `Worker.convert` does: a little-endian 32-bit length, then
   the JSON request `{"version": 1, "jobId": ..., "html": ..., "options": ...}` with the harness's
   protocol options and `generateTaggedPdf` set per table. Read the response header frame, then PDF
   frames until the zero-length frame. Discard the first job as a warmup.
5. Time from the start of `docker run` to the zero-length frame (*PDF complete*) and until the
   container no longer exists (*gone*). For warm workers, time each job from its request. For the
   empty sandbox, override the entrypoint with `/bin/true` and time the whole `docker run --rm`.
6. For section 6, pass `--security-opt seccomp=<profile>` and start Chromium without `--no-sandbox`.
   Alternate sandbox-off and sandbox-on workers within each round.
