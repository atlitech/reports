# Isolated renderer experiment

Status: research, frozen. The experiment merged in #152 has the public API launch a fresh gVisor
container for every conversion. It priced the strongest boundary available to it. It is not the
production architecture. The default server remains the integrated engine for self-hosted,
application-owned reports. This page records the decision, the measurements behind it, the hosted
design that replaces the experiment's shape, and how to reproduce the experiment.

## Status and decision

In the experiment, the API process runs `docker run --runtime=runsc` once per request
([`WorkerLauncher.cs`](../src/Atli.Reports.Server/Execution/WorkerLauncher.cs)). That shape is
wrong for production in two ways, and the self-hosted product does not need it:

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

1. Self-hosted deployments use the integrated engine; it is the supported mode. A separate change
   turns Chromium's sandbox on by default in the server image. Operators who want defense in depth
   can run the same image under a sandboxed runtime, such as a gVisor or Kata RuntimeClass.
   Chromium's sandbox does not start under gVisor on arm64 today, so on gVisor they currently trade
   one layer for the other.
2. A managed service, if one is built, puts a shared API in front of per-customer renderer
   deployments. See [Hosted renderer design](#hosted-renderer-design).
3. Keep the private protocol, the worker binary, the tests, and the benchmark harness from #152.
   Freeze the Docker launcher backend as research; do not extend it.

## What the measurements show

### Initial measured outcome

The [ARM64 exploratory run](../benchmarks/results/2026-10-02-5c500701-isolated-workers-arm64.md)
completed all 96 conversions. With one CPU and 1 GiB per container, median time for the 49-page
report was 1.57 seconds in the integrated engine, 16.63 seconds in a disposable gVisor worker,
11.36 seconds in a warm gVisor worker, and 3.04 seconds in the equivalent warm runc control.
The warm gVisor worker consumed 66.14 CPU seconds across 24 reports including warmups, compared
with 18.03 seconds for the runc control; those observations exclude final teardown and the gateway.

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

- **The recorded warm gVisor numbers were pessimistic.** A fresh warm gVisor worker per fixture on
  `overlay2` took 4.37 s for the tagged 49-page report (recorded: 11.36 s), 0.21 s for the chart
  (0.60 s), and 0.21 s for the invoice (0.29 s). The recorded run used `vfs` and ran all four
  fixtures in one worker, whose cgroup memory peak reached about 805 MiB of its 1 GiB limit. Which
  difference explains the gap was not isolated.
- **Startup and teardown dominate single-use workers.** An empty gVisor sandbox's full lifecycle
  took 0.74 s on `vfs` and 0.26 s on `overlay2`. A single-use gVisor worker finished the invoice
  PDF 1.50 s (`vfs`) or 1.06 s (`overlay2`) after `docker run` started, and its container was gone
  0.29 s or 0.18 s later. A single-use runc worker finished in 0.26 s and was gone at 0.34 s. The
  recorded disposable gVisor p50 was 2.25 s on `vfs`, including the harness's cleanup commands.
- **Teardown sits on the response path.** The gateway waits for the worker to exit, which follows
  graceful browser shutdown, and removes the container before `ConvertAsync` returns. The endpoint
  completes the HTTP response only after that, so the teardown tail is client-visible latency and
  holds a job slot.
- **The storage driver matters.** The harness's nested daemon uses `vfs`, which added about 0.5 s
  to every sandbox lifecycle compared with `overlay2`. Measure on the storage driver production
  would use.
- **Tagged PDFs cost time on long reports.** Under runc, tagging made the 49-page report 7.19 MB
  instead of 0.54 MB and took 1.899 s instead of 1.076 s. Under gVisor it took 4.37 s instead of
  3.36 s. One-page fixtures showed no material difference. The harness and the load benchmark
  always tag, and the integrated engine tags unless a request sets `generateTaggedPdf` to false.
- **gVisor still costs render time on long reports.** In the clean probes the 49-page report took
  2.3 times as long under gVisor as under runc tagged, and 3.1 times untagged. The runc probes used
  the host daemon and the gVisor probes a nested one. A later run of the same untagged gVisor
  configuration took 5.04 s instead of 3.36 s, and successive rounds kept slowing, so this laptop
  VM cannot size CPU or memory budgets.
- **The browser process model is not the lever.** Under gVisor, reduced process models
  (`--no-zygote`, `--single-process`, one renderer process without site isolation) shortened a
  cold one-page command-line print from 1.79 s to as little as 1.34 s. They did not materially
  change the long report (7.25 to 7.84 s), and they weaken Chromium's own isolation.
- **Chromium's sandbox is cheap under runc.** Docker's default seccomp profile makes Chrome abort
  with "No usable sandbox!", which is why both images pass `--no-sandbox` today. With a probe
  profile that added `clone`, `unshare`, and `chroot` to Docker's default, Chrome started with its
  sandbox and the worker converted every fixture, with all other container hardening kept. Five
  interleaved rounds showed about 30 ms extra at cold start and warm differences within noise in
  both directions. OrbStack's kernel permits unprivileged user namespaces; hosts that restrict them,
  such as Ubuntu 23.10 and later through AppArmor, were not tested.
- **Chromium's sandbox does not run inside gVisor on arm64.** Chrome's own seccomp-bpf SIGSYS
  handler crashes on arm64 syscall 123 (`sched_getaffinity`), and the process hangs until killed.
  The worker image's `ATLI_WORKER_NO_SANDBOX=true` is therefore currently required under gVisor on
  arm64, not merely convenient. amd64 was not tested.
- **gVisor's KVM platform was not tested.** OrbStack exposes no `/dev/kvm`; every gVisor number
  here uses systrap.

### Reading the results now

The recorded run's conclusion stands: keep the integrated default. Three parts of its reading
change. First, the recorded gVisor figures overstate both lifecycle and render cost compared with
cleaner probes on the same host. The warm long-report ratio to runc was 3.7 in the recorded run
and 2.3 (tagged, runc on the host daemon) in the follow-up. Second, most of the per-job cost for
small reports is sandbox and browser lifecycle, storage driver, and teardown, not rendering. Warm
renderers remove it; a faster per-job launcher would only shrink it. Third, a gVisor render
overhead on long reports appears in every run, but its size is not stable on a laptop VM and must
be measured on the intended platform.

## Hosted renderer design

This is the agreed design if a managed service is built. Nothing in this repository implements it
yet.

```mermaid
flowchart LR
    CA[Customer A application] -->|Authenticated conversion| API
    CB[Customer B application] -->|Authenticated conversion| API
    subgraph apipool [API node pool]
        API[Shared API: authentication, product tenant, quotas, routing, PDF relay]
    end
    subgraph rendererpool [Renderer node pool: no egress, no secrets]
        RA[Customer A renderer deployment]
        RB[Customer B renderer deployment]
    end
    API -->|Mutually authenticated job| RA
    API -->|Mutually authenticated job| RB
    RA -->|Untrusted PDF stream| API
    RB -->|Untrusted PDF stream| API
```

A shared public API authenticates callers, resolves product-tenant membership, enforces quotas,
routes each job, and relays the streamed PDF to the caller. It never parses or executes document
HTML. Each customer has its own renderer deployment behind it.

### What a renderer runs

A renderer runs the existing server image in integrated mode: a warm browser, a fresh browser
context per conversion, and Chromium's sandbox on. It accepts conversions only from the API's
identity. It receives document HTML and PDF options, never customer credentials.

### Separation requirements

- Ingress is internal only and reachable only from the API.
- Network policy denies renderer-to-renderer traffic and all renderer egress. Documents must be
  self-contained; fetching approved remote assets would need its own design.
- Renderers have no application secrets, no service identity, and no mounted service-account
  token. Their root filesystem is read-only, with bounded temporary storage.
- Renderers run on a node pool separate from the API, enforced with taints and affinity, so a
  kernel escape lands among renderers rather than next to the API's secrets. Large customers can
  have dedicated nodes.
- The API and renderers authenticate each other, with mTLS or managed identity.
- The API treats every renderer response as untrusted input: bounded size, checked framing and
  completion, a PDF prefix check, and an aborted client response if the renderer fails mid-stream.
  The #152 gateway already applies these checks to worker output.

### Routing

The API maps the caller's authenticated product tenant to that customer's renderer. It never takes
the renderer from a request header, a body field, or a caller-supplied renderer ID. A renderer
serves one customer for its entire lifetime; moving capacity to another customer requires
destroying the renderer first.

On Azure Container Apps, apps in one environment can reach each other, and there are no network
policies between them. A renderer there must authenticate the API itself, and egress must be
blocked at the VNet level; alternatively, renderers get their own environment. On Kubernetes, the
cluster's CNI must actually enforce NetworkPolicy. These platform properties are design inputs;
this repository's tests have not verified them.

### Scaling and cost

In the recorded run, the warm runc control's cgroup memory peak was 353,558,528 bytes (about
337 MiB, page cache included) after its first fixture, the invoice, and 906,895,360 bytes (about
865 MiB) after the 49-page report. A warm renderer's footprint is therefore a few hundred MiB even
for small reports, and its memory limit follows the largest documents a customer sends.

Larger customers keep renderers always on. Small customers can scale to zero and accept a slow
first request. Locally, a fresh runc container returned the invoice PDF 0.26 s after `docker run`
started, but platform scheduling of a new replica is expected to take seconds; that was not
measured.

### Runtime options per renderer

| Runtime | Boundary between customers | Speed in these probes | Chromium's sandbox |
| --- | --- | --- | --- |
| Plain containers (runc) with Chromium's sandbox | Customers on a node share its kernel. Crossing customers needs a renderer exploit, a sandbox escape, and a kernel or container escape. | Near native; the sandbox added about 30 ms at cold start | Worked in the probe with a seccomp profile that permits it; AppArmor-restricted hosts untested |
| gVisor | Stronger: the renderer's system calls go to gVisor's user-space kernel, not the host's | The long report took 2.3 to 3.1 times as long as under runc in clean probes; systrap only | Does not start on arm64; amd64 untested |
| MicroVMs: Kata, Firecracker, or Hyper-V-backed Azure Container Apps dynamic sessions | Stronger: each sandbox runs its own guest kernel | Not measured | A normal guest kernel should support it; untested |

Only the runc and gVisor rows rest on measurements, and those come from an ARM64 laptop VM. The
platform properties in this table have not been verified by this repository's tests.

### Why Chromium's sandbox alone is not the boundary between customers

- In a hosted service an attacker can simply be a customer. It submits exploit code directly and
  as often as it likes; no victim has to open a page.
- A document can print `navigator.userAgent` into its own PDF and learn the exact Chrome build. The
  exposure window for a public Chrome vulnerability is the time between the Chrome security release
  and the renderer's redeploy.
- Chains of a renderer bug and a sandbox escape are exploited in the wild every year.
- The integrated engine shares one browser process across all conversions. Browser contexts
  separate document storage, not process privilege, so one escape reaches every in-flight document
  in that browser.

Chromium's sandbox is therefore one layer. The per-customer renderer bounds what one escape
reaches, and the runtime and node pool decide how hard the next step is.

### Exception: end users who distrust each other

If one customer's documents come from parties that distrust each other, for example when the
customer lets its own end users upload raw HTML, the trust domain is the end user, not the
customer. That calls for per-job or per-user isolation delivered by the platform, not by the API
shelling out to Docker.

### What to keep from #152

- The private protocol: bounded framing, worker output treated as untrusted, and streaming
  ([`src/Atli.Reports.Worker.Protocol`](../src/Atli.Reports.Worker.Protocol)).
- The worker binary ([`src/Atli.Reports.Worker`](../src/Atli.Reports.Worker)).
- The tests
  ([`tests/Atli.Reports.Engine.Tests/Workers`](../tests/Atli.Reports.Engine.Tests/Workers)).
- The benchmark harness
  ([`validate-isolated-workers.py`](../.github/scripts/validate-isolated-workers.py)) and the
  [isolated worker workflow](../.github/workflows/isolated-workers.yml).

The Docker launcher backend is frozen as research. It remains for reproducing these measurements;
do not extend it or deploy it as a hosting backend.

## Production acceptance gates

Before accepting hostile documents in a hosted service:

1. Validate each renderer runtime on the actual deployment platform. From a deliberately
   compromised renderer, test direct network access (other renderers, the API's other endpoints,
   metadata endpoints, the internet), filesystem, identity, process, and control-plane access, in
   addition to normal document policy tests. Verify that Chromium's sandbox is active wherever the
   runtime supports it. Verify resource exhaustion, cancellation, and crash recovery. Runtime
   canaries establish specific controls, not the absence of sandbox vulnerabilities.
2. Introduce authoritative product-tenant membership and routing. Derive the renderer from
   authenticated entitlements, never a request header or caller-supplied renderer ID. Enforce fair
   admission and quotas across API replicas. Authenticate the API-to-renderer channel in both
   directions, and confirm that a renderer rejects every other caller.
3. Verify separation on the real cluster or environment: enforced network policy (on Azure
   Container Apps, VNet-level egress blocking or a separate environment), node-pool placement, and
   the absence of secrets, identities, and service-account tokens in renderers.
4. Measure on the intended platform and storage: sustained and burst load, cold start of a new
   replica, warm latency, CPU time per report, peak memory per renderer, idle cost, and failure
   rate. Choose always-on and scale-to-zero policies, replica limits, and scaling rules from those
   results; cap total cost and shed load explicitly.
5. Set and meet a browser patch target: the time from a Chrome security release to redeployed
   renderers, since any document can read the exact build.
6. Add lifecycle observability and operator recovery without logging HTML, PDF content, credentials,
   or sensitive URLs. Establish incident response and adversarial tests. Durable asynchronous jobs
   additionally require a durable queue and authorized, expiring storage.

Kubernetes renderer deployments need enforcing network policy on their actual nodes, and a tested
RuntimeClass if a sandboxed runtime is selected. Azure Container Apps' ordinary container profile
is not interchangeable with a dedicated sandbox service; an Azure-specific deployment needs its own
lifecycle and boundary tests. The existing Kubernetes and ACA deployment examples continue to
describe integrated self-hosting.

## Open questions and next measurements

- Decide the tagged-PDF default. It also affects the integrated engine, where Chromium tags unless
  `generateTaggedPdf` is false.
- Repeat the measurements on a dedicated, idle Linux x86-64 host with `overlay2`, with interleaved
  rounds.
- Add per-phase worker timings: sandbox start, browser launch, load, print, and stream.
- Test gVisor's KVM platform where `/dev/kvm` exists.
- Test Chromium's sandbox inside gVisor on amd64, and inside a microVM runtime.
- Verify whether Azure Container Apps supports the user namespaces Chromium's sandbox needs.
- Choose a mutual-authentication mechanism that works without renderer egress or a renderer
  service identity. API-key verifiers need neither; validating the API's managed-identity tokens
  needs signing-key metadata, and mTLS needs certificate distribution.
- Measure concurrent load on a warm renderer.

## The experiment as built

The rest of this page describes the research code merged in #152.

### Trust boundary

The experiment keeps the public `POST /convert` contract and `IHtmlToPdfConverter` interface. An
optional execution backend forwards a bounded job to a private worker. Authentication,
permissions, request limits, and caller identity stay in the API. Workers receive HTML, PDF
options, a protocol version, and a generated correlation ID; they receive no API key, access
token, tenant header, or reusable cloud credential. The correlation ID identifies a response, not
an authorized tenant.

```mermaid
flowchart LR
    C[Calling application] -->|Authenticated conversion| A[API: authorization and admission]
    A --> L[Trusted launcher]
    L -->|Bounded private job stream| W[Renderer inside runtime sandbox]
    W -->|Bounded PDF frames| A
    A -->|Streaming PDF| C
```

The API must treat a compromised worker's response as untrusted input. Frame lengths, protocol
version, job identity, error kinds, output size, completion, and process exit are checked. PDF
bytes stream to the client instead of accumulating in the gateway. A worker failure after the
response starts aborts that response; it cannot become a successful truncated download.

Browser contexts still separate ordinary document state. The outer runtime boundary must contain
a compromised renderer. A separate process alone does not supply that boundary. The development
process backend therefore requires explicit opt-in. The experimental Docker backend requires a
configured sandbox runtime (`runsc` by default); an unavailable runtime must fail instead of
silently selecting ordinary Docker isolation.

Docker workers run without networking, host mounts, a daemon socket, application secrets, or a
service identity. Their root filesystem is read-only, writable temporary storage is bounded, and
CPU, memory, process count, elapsed time, input, output, and concurrent jobs have explicit limits.
Only self-contained assets are supported in this experiment. The integrated engine's asset
allowlist does not enable networking in workers.

The Docker launcher has authority over its configured daemon. In this prototype it runs in the
API process, so compromising that API can compromise the daemon's workload domain. Use a dedicated
test host or daemon; this is not a least-privilege cloud scheduler. This authority is why the
shape sits in the wrong layer; the hosted design removes container launching from the API instead
of narrowing it. Never mount the daemon socket into the renderer or present this prototype as
containing an API exploit.

### Selecting the execution backend

The Docker backend is frozen as research. These settings remain for reproducing the measurements.

Authentication is configured exactly as described in the [security guide](security.md). Execution
settings bind from `ReportsServer:Execution`; environment variables use `__` separators. The mode
defaults to `InProcess`. To select the experiment, use an operator-controlled configuration:

```text
ReportsServer__Execution__Mode=Worker
ReportsServer__Execution__Backend=Docker
ReportsServer__Execution__DockerExecutablePath=/usr/bin/docker
ReportsServer__Execution__Image=<tested-worker-image@sha256:digest>
ReportsServer__Execution__Runtime=runsc
```

The API host must have the Docker CLI at that absolute path and access to a dedicated daemon with
the runtime installed and worker image available. The existing server image does not install the
Docker CLI. These settings alone do not provision a sandbox or convert the ACA/Kubernetes examples
into worker deployments. Build and test the worker image from this checkout; no public worker
image or package is promised by this experiment.

| Execution setting | Default | Meaning |
| --- | --- | --- |
| `MaxConcurrentJobs` | 2 | Active worker slots per API process; saturation returns `Busy` |
| `MaxPdfBytes` | 52428800 | Maximum streamed output bytes per job |
| `Timeout` | `00:01:30` | Worker operation deadline; the request admission deadline can be tighter |
| `CleanupTimeout` | `00:00:05` | Bounded cleanup attempt after completion, cancellation, or failure |
| `MemoryLimitBytes` | 1073741824 | Docker worker memory limit, with swap disabled |
| `CpuLimit` | 1 | Docker CPU limit per worker |
| `PidsLimit` | 256 | Docker process limit per worker |

These are starting budgets, not measured capacity guarantees. The API's body, caller, and global
admission limits still apply. The private protocol additionally caps a serialized request at
16 MiB, a response header at 16 KiB, and a PDF frame at 64 KiB. Increasing HTTP limits cannot
override those protocol limits. Cleanup failure requires operator recovery rather than admitting
more work with uncertain live workers.

`Backend=Process` requires `AllowDevelopmentProcess=true` and an absolute
`ProcessExecutablePath`; `ProcessArguments` are static operator arguments. This mode is for
development and protocol tests and does not contain malicious child processes. Do not put HTML or
credentials in arguments. `EnvironmentVariables` explicitly configures the launched process;
under Docker it configures the trusted CLI and is not forwarded into the renderer container.
Only add settings needed by the launcher or local development worker, never customer credentials.

The worker reads only its explicit operational variables: `ATLI_WORKER_BROWSER_PATH` (an absolute
browser path), `ATLI_WORKER_NO_SANDBOX` (default `false`),
`ATLI_WORKER_DISABLE_DEV_SHM_USAGE` (default `true`), and `ATLI_WORKER_MAX_JOBS` (default 1).
It does not load server appsettings, authentication configuration, or general `ReportsEngine`
environment settings. Input acquisition is limited to 15 seconds, rendering to 90 seconds per
job, and the worker lifetime to 300 seconds, with a separate bounded shutdown attempt. The parent
launcher enforces its deadline independently of the worker's cooperative timers. Disabling the
inner Chromium sandbox in the worker image relies on the separately tested outer runtime boundary.
Under gVisor on arm64 that choice is currently forced, because Chromium's sandbox crashes there.

The launcher supplies Docker's `--init`; the image starts the native worker directly. Its fixed
browser launcher uses `setsid --wait` to put Chromium in a separate session. Actual gVisor testing
caught browser process-tree termination sending SIGHUP to the worker's shared process group after
PDF completion. Separating the browser session preserves clean worker exit without suppressing
signals or accepting a failed exit code. This is lifecycle management, not the sandbox boundary.

### Measurement plan

The gateway backend uses one disposable worker per job with immediate rejection when all worker
slots are occupied. There is no hidden unbounded queue, retry of a partially streamed PDF, or
durable delivery guarantee. Worker exit and container removal complete before the response does.

The experiment compares four cases with identical reports, PDF settings, and resource budgets:

| Case | What it measures |
| --- | --- |
| Warm integrated engine | Existing self-hosted baseline with fresh browser contexts |
| Disposable sandboxed worker | Runtime, process, browser startup, and rendering per job |
| Sequential warm sandboxed worker | Rendering with browser reuse inside one explicit trust domain |
| Sequential warm ordinary Docker worker | Same private transport and image, to compare runtime overhead |

The worker's operator-only `ATLI_WORKER_MAX_JOBS` setting permits a bounded sequential reuse
experiment (default 1, maximum 100). The gateway still sends one job and closes its input. This
setting does not create a public worker pool or select tenants. A failed job terminates the worker;
per-job and overall lifetime limits prevent unlimited reuse.

Use the repository's invoice, long-table, asynchronous chart, and embedded-image fixtures. Record
actual page counts and output sizes, whether the PDF is tagged, warmup policy, sample count,
p50/p95 latency, throughput, errors, runtime and Chrome versions, storage driver, architecture,
and resource limits. Report CPU and memory where they can be observed, including the limits of the
measurement method. A short feasibility run does not establish a production p95 SLO or peak
capacity. End-to-end HTTP and private-stream measurements have different transport costs and must
be labeled accordingly.

No numeric latency or concurrency target has been selected yet. Use measured results and customer
report distributions to set those targets. Favor low allocation and streaming on the gateway,
bounded backpressure, and keeping ready capacity when startup dominates. Do not trade isolation
between customers for a faster benchmark.

### Reproducing the experiment

The local harness requires Linux or macOS, Docker, Python 3.12 or newer, and the repository's .NET
SDK. It starts a uniquely named, privileged Docker-in-Docker daemon as trusted test
infrastructure, installs a checksum-verified gVisor bundle there, and removes its owned containers
on completion. It does not change the default Docker daemon's runtime configuration, mount its
socket into workers, or expose a Docker API port. Privileged DinD is a test control plane, not the
worker security boundary.

```bash
dotnet build src/Atli.Reports.Server
docker build -f src/Atli.Reports.Worker/Dockerfile -t atli-reports-worker:security .
docker build -f src/Atli.Reports.Server/Dockerfile -t atli-reports-server:security .
python3 -B .github/scripts/smoke-test-worker-gateway-aot.py \
  atli-reports-server:security atli-reports-worker:security
python3 -B .github/scripts/validate-isolated-workers.py --samples 5
```

The first smoke test uses the explicitly enabled development process backend to check NativeAOT
gateway configuration and the private protocol. The second uses real gVisor workers and the
Docker backend. It tests direct containment canaries with a reachable network control, gateway
authorization, PDF completion, policy rejection, disconnects, worker crashes, deadlines, and
missing-runtime rejection. Final results are written only after clean worker exit and teardown.
`--skip-benchmark` runs the latter checks without timings. `--output` changes the JSON result path
(default `artifacts/isolated-worker-results.json`).

The harness's nested daemon uses the `vfs` storage driver, and the harness requests tagged PDFs.
The first lengthens every sandbox lifecycle and the second lengthens long reports; see the
follow-up findings above.

The [isolated worker workflow](../.github/workflows/isolated-workers.yml) runs native amd64 and
arm64 image builds and these checks. It publishes no image and deploys no cloud resources. CI's
three samples per fixture are a feasibility check, not a stable performance regression threshold.
Run measurements on an otherwise idle host and retain the image identities, source revision,
limits, sample counts, and measurement caveats with the results.

Background: [gVisor architecture](https://gvisor.dev/docs/architecture_guide/intro/),
[gVisor compatibility](https://gvisor.dev/docs/user_guide/compatibility/),
[Kubernetes multi-tenancy](https://kubernetes.io/docs/concepts/security/multi-tenancy/), and
[Azure custom container sessions](https://learn.microsoft.com/en-us/azure/container-apps/sessions-custom-container).
