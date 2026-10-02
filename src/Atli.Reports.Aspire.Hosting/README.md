# Atli.Reports.Aspire.Hosting

An [Aspire](https://aspire.dev) hosting integration for the
[Atli Reports server](https://github.com/atlitech/reports/blob/main/docs/engine/server.md), the
container that converts HTML to PDF over HTTP. One call in the AppHost runs the server with its
health check, its telemetry in the dashboard, and the connection string
[Atli.Reports.Client](https://www.nuget.org/packages/Atli.Reports.Client) reads.

Part of [Atli Reports](https://github.com/atlitech/reports). Needs Aspire 13.0 or later and an
AppHost that targets .NET 10.

## Install

In the AppHost project:

```bash
dotnet add package Atli.Reports.Aspire.Hosting
```

## Use

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var reports = builder.AddReportsServer("reports").WithDevelopmentApiKey();

builder.AddProject<Projects.Api>("api").WithReference(reports).WaitFor(reports);

builder.Build().Run();
```

In the app, `builder.AddReportsClient("reports")` from `Atli.Reports.Client` registers
`IHtmlToPdfConverter` over the server.

`WithDevelopmentApiKey` generates a fresh random secret for each local run. It wires the server's
hashed verifier, client credentials, and dashboard test command. It refuses publishing. For
deployment, supply separate secret parameters containing the full API credential and base64
SHA-256 of that entire credential:

```csharp
var apiKey = builder.AddParameter("reports-api-key", secret: true);
var apiKeyHash = builder.AddParameter("reports-api-key-hash", secret: true);
var reports = builder.AddReportsServer("reports")
  .WithApiKeyAuthentication("api", apiKey, apiKeyHash, callerId: "my-application");
```

The credential must be `api.<random secret>` with at least 32 random bytes of entropy. The helper
grants `reports.convert`. The server receives the hash; authorized referenced applications receive
the full credential through secret parameter references. Protect application configuration and use
distinct caller identities for independent applications. Never check credentials into source control.
The server requires an explicit authentication mode. `WithAnonymousAccess()` is available only as
an explicit choice for trusted development or a separately enforced authentication boundary.

`AddReportsServer` runs `ghcr.io/atlitech/reports-server`, tagged with this package's version, and:

- exposes the server's port 8080 as the `http` endpoint (pass `port` to fix the host port);
- marks the resource healthy once `/health/ready` answers `200`; the server answers `503` while its
  browser fails to launch, so `WaitFor` waits for a server that can convert;
- probes `/health/ready` for readiness and `/health/live` for liveness on deployment targets that
  run probes, such as Azure Container Apps (not on Kubernetes yet, see
  [Deploy](https://github.com/atlitech/reports/blob/main/docs/aspire.md#deploy));
- sends the server's logs, metrics, and traces to the dashboard over OTLP;
- links the server's OpenAPI document, `/openapi/v1.json`, which requires `reports.diagnostics`;
- adds a `Convert a test page` dashboard command, which converts a one-page document and logs the
  PDF's size and the time it took.

`WithReference` passes the connection string `Endpoint=<url>;ApiKey=<credential>` when API-key
authentication is configured (otherwise only `Endpoint=<url>`) as `ConnectionStrings__reports`, and
the connection properties as `REPORTS_HOST`, `REPORTS_PORT`, and `REPORTS_URI` for apps in other
languages.
With API-key authentication it also provides the secret `REPORTS_APIKEY` connection property.
The test-page command is disabled without an API-key helper or explicit anonymous mode; JWT-only
deployments should send an authorized conversion from their application. Readiness probes stay
anonymous and do not include credentials.

## Configure

| Method | Server setting | Server default |
| --- | --- | --- |
| `WithMaxConcurrentConversions(4)` | `Concurrency:MaxConcurrentConversions` | processors, 2 to 8 |
| `WithMaxQueueLength(50)` | `Concurrency:MaxQueueLength` | 100 |
| `WithQueueTimeout(TimeSpan.FromSeconds(10))` | `Concurrency:QueueTimeout` | 30 seconds |
| `WithConversionTimeout(TimeSpan.FromSeconds(90))` | `ConversionTimeout` | 1 minute |
| `WithBrowserRecycling(maxConversionsPerProcess: 500, maxProcessLifetime: TimeSpan.FromMinutes(30))` | `Browser:MaxConversionsPerProcess`, `Browser:MaxProcessLifetime` | 1000, 1 hour |

Each method sets the matching `ReportsEngine__*` environment variable and rejects values the server
would refuse. Set any other engine setting with `WithEnvironment`, for example
`.WithEnvironment("ReportsEngine__Browser__IdleTimeout", "00:05:00")`.

## Another version, or the server built from source

```csharp
builder.AddReportsServer("reports").WithImageTag("0.26.1");

builder
  .AddReportsServer("reports")
  .WithDockerfile("../reports", "src/Atli.Reports.Server/Dockerfile");
```

`WithDockerfile` builds the image from a clone of the repository (the context path is relative to
the AppHost project, the Dockerfile path to the context) and runs it in place of the released
image, locally and when you deploy.

## Learn more

- [Aspire guide](https://github.com/atlitech/reports/blob/main/docs/aspire.md): the AppHost, the
  app, Blazor reports, image versions, and deployment
- [Atli.Reports.Server](https://github.com/atlitech/reports/blob/main/docs/engine/server.md): the
  `/convert` contract and the container image
- [Engine configuration reference](https://github.com/atlitech/reports/blob/main/docs/engine/architecture.md#configuration-reference)
