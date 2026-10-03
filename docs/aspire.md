# Atli Reports with Aspire

Two packages connect an [Aspire](https://aspire.dev) app to the
[Atli Reports server](engine/server.md):

- **`Atli.Reports.Aspire.Hosting`**, in the AppHost, runs the server container and hands its
  address to the apps that reference it.
- **`Atli.Reports.Client`**, in each app, implements `IHtmlToPdfConverter` over that server.

Apps then convert HTML (or Blazor reports) without a browser of their own, and the server's logs,
metrics, and traces show up in the Aspire dashboard next to theirs.

The reference consumer is [`examples/RemoteReportServer`](../examples/RemoteReportServer): an app
with no browser that renders Blazor reports and converts any HTML on the server. This repository's
AppHost, [`examples/Atli.Reports.AppHost`](../examples/Atli.Reports.AppHost), runs it against a server
built from source, and [an end-to-end test](../tests/Atli.Reports.AppHost.Tests) starts both and
checks that the app returns PDFs without starting a browser.

## Requirements

- Aspire 13.0 or later. The hosting package depends on `Aspire.Hosting` 13.0.0 or later, so your
  AppHost keeps the Aspire version it already has.
- An AppHost that targets .NET 10 (`net10.0`), like the hosting package.
- Docker or Podman on the machine that runs the AppHost.
- The packages and the image are released together, starting with 0.26.0. Until then, build the
  server from source (see [below](#build-the-server-from-source)).

## The AppHost

```bash
dotnet add package Atli.Reports.Aspire.Hosting
```

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var reports = builder.AddReportsServer("reports").WithDevelopmentApiKey();

builder.AddProject<Projects.Api>("api").WithReference(reports).WaitFor(reports);

builder.Build().Run();
```

`AddReportsServer(name, port)` adds a `ReportsServerResource`, a container resource with:

| | |
| --- | --- |
| Image | `ghcr.io/atlitech/reports-server:<version>`, where `<version>` is the hosting package's version |
| Endpoint | `http`, to the server's port 8080 in the container. `port` fixes the host port; by default Aspire picks one. The endpoint is not external. |
| Health | Healthy once `GET /health/ready` answers `200`: the server's browser can launch and recent conversions mostly succeed. The image launches the browser at startup. While a launch fails, the status-only probe answers `503` and the server retries in the background. `WaitFor(reports)` keeps waiting until a launch succeeds. Authorized operators can inspect `/health/details` for the reason. See [the server's health endpoints](engine/server.md#health). |
| Probes | When the app deploys, a readiness probe on `/health/ready` and a liveness probe on `/health/live`, for the targets that run probes, such as Azure Container Apps. Not on Kubernetes yet; see [Deploy](#deploy). |
| Seccomp | When the AppHost runs, `--security-opt seccomp=<file>` with the profile Chromium's sandbox needs ([`deploy/seccomp/chromium.json`](../deploy/seccomp/README.md), which the package carries and copies to the local application data directory). Deployment targets need it configured; see [Deploy](#deploy). |
| Telemetry | Logs, metrics, and traces go to the dashboard over OTLP, including the engine's `atli.reports.convert` spans below each `POST /convert`. |
| OpenAPI | The dashboard links `/openapi/v1.json`, which requires `reports.diagnostics`. The link does not attach credentials. Development and production API-key helpers grant conversion permission only; use an operator credential for diagnostics. |
| Command | `Convert a test page` converts a one-page document and logs its PDF size and duration. It is enabled while the server is healthy and an API-key helper or explicit anonymous access is configured. From a terminal: `aspire resource reports convert-test-page`. JWT-only deployments use the calling application's token flow. |

`WithReference(reports)` gives the app these environment variables:

| Variable | Example | For |
| --- | --- | --- |
| `ConnectionStrings__reports` | `Endpoint=http://localhost:51006;ApiKey=<credential>` | `AddReportsClient("reports")`; treat the entire value as a secret |
| `REPORTS_URI` | `http://localhost:51006` | apps in other languages |
| `REPORTS_HOST`, `REPORTS_PORT` | `localhost`, `51006` | apps in other languages |
| `REPORTS_APIKEY` | `<credential>` | Secret header value for `X-Reports-Api-Key` when an API-key helper is configured |

The connection string's name is the resource's name, and the other variables start with it in
upper case. A plain name such as `reports` keeps them easy to read from any language. Without an
API-key helper, the connection string contains only `Endpoint` and there is no `REPORTS_APIKEY`.
The URI, host, and port never include credentials.

## The app

```bash
dotnet add package Atli.Reports.Client
```

```csharp
using Atli.Reports.Client;
using Atli.Reports.Engine;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddReportsClient("reports");

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet(
  "/hello.pdf",
  async (IHtmlToPdfConverter converter, CancellationToken cancellationToken) =>
  {
    var result = await converter.ConvertAsync(
      "<!DOCTYPE html><h1>Hello, PDF</h1>",
      new PdfOptions { PaperSize = PaperSize.A4 },
      cancellationToken
    );

    return result.Match<IResult>(
      pdf => Results.File(pdf, "application/pdf", "hello.pdf"),
      error => Results.Problem(error.Message)
    );
  }
);

app.Run();
```

`AddReportsClient("reports")` reads `ConnectionStrings:reports`, the variable the AppHost set. It
retries the server's `503` answers and connection failures, removes the service defaults'
standard resilience handler from its own `HttpClient` (that handler's 10-second timeout would cut
conversions short), and adds a `reports_server` health check, tagged `ready`, that probes the
server's `/health/ready`. See [the client's README](../src/Atli.Reports.Client/README.md) for its
settings and how server errors map to `ConversionError`s.

## Blazor reports on the server

With `Atli.Reports.Blazor`, components render in the app and the server converts them, so the app
needs no browser:

```csharp
using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddBlazorReports();
builder.AddReportsClient("reports");

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapBlazorReport<HelloReport, HelloReportData>(); // POST /helloreport

app.Run();
```

`AddReportsClient` replaces the engine's converter whichever of the two calls comes first, and turns
off the engine's browser warm-up. `IReportService` and `MapBlazorReport` work as they do with the
in-process engine. [`examples/RemoteReportServer`](../examples/RemoteReportServer) is this app in
full, with a Blazor report and an endpoint that converts any HTML through `IHtmlToPdfConverter`.

## Authentication

The server requires explicit authentication. `WithDevelopmentApiKey()` generates ephemeral secret
parameters for local runs and passes the credential through `WithReference`; publishing this helper
fails deliberately. Use secret parameters with `WithApiKeyAuthentication(keyId, apiKey, apiKeyHash)`
for deployment, or configure JWT authentication and acquire tokens in the application. The hash is
base64 SHA-256 of the entire `id.secret` credential. See the [hosting package](../src/Atli.Reports.Aspire.Hosting/README.md)
and [security guide](security.md) for production configuration and key rotation. Anonymous local
access is an explicit `.WithAnonymousAccess()` choice.

The server blocks external document networking by default. Inline report assets or configure an
explicit public-origin allowlist; this is a server policy, not a `PdfOptions` setting.

## Configure the server

The server reads the engine's settings from `ReportsEngine__*` environment variables. Typed methods
set the common ones and reject values the server would refuse to start with:

| Method | Server setting | Server default |
| --- | --- | --- |
| `WithMaxConcurrentConversions(int)` | `Concurrency:MaxConcurrentConversions`; at least 1 | processors, 2 to 8 |
| `WithMaxQueueLength(int)` | `Concurrency:MaxQueueLength`; `0` rejects at once when every slot is taken | 100 |
| `WithQueueTimeout(TimeSpan)` | `Concurrency:QueueTimeout`; not negative, or infinite | 30 seconds |
| `WithConversionTimeout(TimeSpan)` | `ConversionTimeout`, queue wait included; positive, or infinite | 1 minute |
| `WithBrowserRecycling(maxConversionsPerProcess, maxProcessLifetime)` | `Browser:MaxConversionsPerProcess` (`0` never) and `Browser:MaxProcessLifetime` (positive, or infinite); pass either or both | 1000, 1 hour |

```csharp
var reports = builder
  .AddReportsServer("reports")
  .WithMaxConcurrentConversions(4)
  .WithMaxQueueLength(50)
  .WithQueueTimeout(TimeSpan.FromSeconds(10))
  .WithConversionTimeout(TimeSpan.FromSeconds(90))
  .WithBrowserRecycling(maxConversionsPerProcess: 500, maxProcessLifetime: TimeSpan.FromMinutes(30));
```

`Timeout.InfiniteTimeSpan` means no limit where the table allows it. A second call to the same
method replaces the value. Any other setting from the
[configuration reference](engine/architecture.md#configuration-reference) goes through
`WithEnvironment`:

```csharp
reports.WithEnvironment("ReportsEngine__Browser__IdleTimeout", "00:05:00");
```

The client's timeouts must outlast the server's limits, so the server's own `503` or `504` arrives
before the client gives up. By default the client waits two minutes per attempt, longer than the
server's one-minute conversion timeout. If you raise the server's limits, raise `AttemptTimeout`
and `TotalTimeout` too:

```csharp
builder.AddReportsClient(
  "reports",
  settings =>
  {
    settings.AttemptTimeout = TimeSpan.FromMinutes(3);
    settings.TotalTimeout = TimeSpan.FromMinutes(6);
  }
);
```

## Image version

The integration runs the server released with it: `Atli.Reports.Aspire.Hosting` 0.26.0 runs
`ghcr.io/atlitech/reports-server:0.26.0`, so updating the package updates the server. A release
publishes the image before the packages, so a published package's tag always exists. The tag keeps
that release's server, and moves only when a Chrome refresh rebuilds the release with a newer
`chrome-headless-shell`, which a container runtime picks up the next time it pulls the tag. To keep
the browser fixed as well, use the `<version>-chrome<chrome>` tag or a digest; see
[the server's published image](engine/server.md#published-image) for the tags and how to verify the
image's provenance.

The standard container methods override it:

```csharp
// Another release of the server.
builder.AddReportsServer("reports").WithImageTag("0.26.1");

// That release with exactly this Chrome version; the tag never moves.
builder.AddReportsServer("reports").WithImageTag("0.26.1-chrome154.0.8037.92");

// An exact image, by digest (without the "sha256:" prefix).
builder.AddReportsServer("reports").WithImageSHA256("<digest>");

// A mirror of the image in your own registry.
builder.AddReportsServer("reports").WithImageRegistry("registry.example.com");
```

## Build the server from source

`WithDockerfile` builds the server from a clone of this repository instead of pulling it. The
context path is relative to the AppHost project and must be the repository root; the Dockerfile
path is relative to the context:

```csharp
builder
  .AddReportsServer("reports")
  .WithDockerfile("../reports", "src/Atli.Reports.Server/Dockerfile")
  .WithBuildArg("CHROME_VERSION", "154.0.8037.92"); // optional; defaults to the version the Dockerfile pins
```

The built image replaces the released one, both when the AppHost runs and when it deploys (the
deployment builds the image and pushes it to the target's registry). Everything else stays: the
endpoint, health check, telemetry, settings, and connection string. The first build compiles the
NativeAOT server and downloads `chrome-headless-shell`, so it takes a few minutes. This
repository's own AppHost, [`examples/Atli.Reports.AppHost`](../examples/Atli.Reports.AppHost),
runs the server this way.

## Deploy

Replace development credentials with production secret parameters first. The server is an ordinary container resource, so Aspire's deployment targets publish it like any
other. Add the target's environment to the AppHost and run `aspire publish` (artifacts only) or
`aspire deploy`:

| Target | Package | AppHost |
| --- | --- | --- |
| Docker Compose | `Aspire.Hosting.Docker` | `builder.AddDockerComposeEnvironment("compose");` |
| Azure Container Apps | `Aspire.Hosting.Azure.AppContainers` | `builder.AddAzureContainerAppEnvironment("aca");` |
| Kubernetes | `Aspire.Hosting.Kubernetes` | `builder.AddKubernetesEnvironment("k8s");` |

For the AppHost at the top of this page plus `AddDockerComposeEnvironment("compose")`,
`aspire publish` writes (abbreviated):

```yaml
services:
  reports:
    image: "ghcr.io/atlitech/reports-server:<version>"
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://compose-dashboard:18889"
      OTEL_EXPORTER_OTLP_PROTOCOL: "grpc"
      OTEL_SERVICE_NAME: "reports"
    expose:
      - "8080"
  api:
    environment:
      ConnectionStrings__reports: "Endpoint=http://reports:8080"
      REPORTS_URI: "http://reports:8080"
    depends_on:
      reports:
        condition: "service_started"
```

- **The server stays internal.** Its endpoint is not external, so only the app reaches it, at the
  container network's address (`http://reports:8080` above). Keep it that way: the server renders
  the application's own HTML, and Chromium's sandbox narrows the damage of a browser bug without
  making it a boundary for hostile HTML. `WithExternalHttpEndpoints()` would publish it.
- **Chromium's sandbox needs a seccomp profile on the target.** The server's browser runs with
  Chromium's sandbox, which needs unprivileged user namespaces that container runtimes deny by
  default. When the AppHost runs, `AddReportsServer` passes the profile it carries to Docker or
  Podman (`--security-opt seccomp=<file>`); publishing does not, because deployment targets do not
  take container runtime arguments. Without the profile the server stays unready and its health
  details say why (see [Chromium's sandbox](security.md#chromiums-sandbox)). Per target, with
  [`deploy/seccomp/chromium.json`](../deploy/seccomp/README.md):

  ```csharp
  // Docker Compose: copy chromium.json next to the published docker-compose.yaml.
  reports.PublishAsDockerComposeService((_, service) => service.SecurityOpt.Add("seccomp=./chromium.json"));

  // Kubernetes: install chromium.json on every node, at
  // /var/lib/kubelet/seccomp/atli-reports/chromium.json (see deploy/kubernetes/reports.yaml).
  reports.PublishAsKubernetesService(service =>
  {
    var pod = service.Workload!.PodTemplate.Spec;
    pod.SecurityContext ??= new();
    pod.SecurityContext.SeccompProfile = new()
    {
      Type = "Localhost",
      LocalhostProfile = "atli-reports/chromium.json",
    };
  });

  // Azure Container Apps cannot apply a seccomp profile: opt out explicitly, for trusted HTML only.
  // Needs `using Azure.Provisioning.AppContainers;`. Only the Container App gets the setting.
  reports.PublishAsAzureContainerApp((_, app) =>
    app.Template.Containers.Single().Value!.Env.Add(new ContainerAppEnvironmentVariable
    {
      Name = "ReportsEngine__Browser__NoSandbox",
      Value = "true",
    }));
  ```

  Use the one for the AppHost's target; Aspire 13.6 refuses to publish the Docker Compose or
  Container Apps customization without that target's environment in the AppHost. These compile
  against Aspire 13.6, and `aspire publish` was checked to write each setting: the `security_opt`
  line, the pod's `seccompProfile`, and the Container App's environment variable. In an AppHost with
  both a Container Apps and a Docker Compose environment that publishes `reports` to Docker Compose,
  the Compose service does not get the opt-out; `WithEnvironment` would set it for every target and
  for local runs. The Kubernetes and Container Apps deployments were not run. Whether Container Apps
  permits user namespaces without the opt-out is unverified.
- **Telemetry** goes wherever the target sends OTLP; the Docker Compose environment adds an Aspire
  dashboard for it.
- **Health in production.** Aspire's health check only drives the local dashboard and `WaitFor`; in
  Docker Compose, `WaitFor` becomes `service_started`. The client copes with a server that is still
  starting: it retries connection failures and `503`s. On targets that run probes, the server has
  two:

  | Probe | Path | Timing |
  | --- | --- | --- |
  | Readiness | `/health/ready` | Every 5 s, 3 s timeout, unready after 3 failures. It fails while the browser cannot launch, and recovers on its own once a background retry succeeds. |
  | Liveness | `/health/live` | Every 10 s, 5 s timeout, restarted after 3 failures. It runs no engine check: a restart does not repair a browser that cannot start, and it drops the conversions in flight. |

  Azure Container Apps runs both, Azure App Service uses `/health/live` as its health check path,
  and Docker Compose runs none. `WithHttpProbe` replaces a probe of the same type, but not on
  `/health/ready`: it also adds a health check on the path, and Aspire rejects the duplicate of the
  resource's own.

  Kubernetes, including Azure Kubernetes Service, gets no probes for now: Aspire's Kubernetes
  publisher writes the probe's scheme in lower case, and Kubernetes then never creates the pod
  ([microsoft/aspire#18271](https://github.com/microsoft/aspire/issues/18271), tracked in
  [#147](https://github.com/atlitech/reports/issues/147)). For a complete standalone container deployment with explicit startup, readiness, and liveness probes, see [the Kubernetes example](../deploy/kubernetes/reports.yaml). When using Aspire, add the probe and correct its scheme:

  ```csharp
  #pragma warning disable ASPIREPROBES001 // Probes are experimental in Aspire 13.
  reports
    .WithHttpProbe(ProbeType.Liveness, "/health/live", periodSeconds: 10, timeoutSeconds: 5)
    .PublishAsKubernetesService(service =>
    {
      foreach (var container in service.Workload!.PodTemplate.Spec.Containers)
      {
        if (container.LivenessProbe?.HttpGet is { } httpGet)
        {
          httpGet.Scheme = httpGet.Scheme.ToUpperInvariant();
        }
      }
    });
  #pragma warning restore ASPIREPROBES001
  ```

- **Sizing.** [The server's throughput figures](engine/server.md#throughput) were measured in a
  container limited to 2 CPUs and 2 GB, a reasonable starting point.
