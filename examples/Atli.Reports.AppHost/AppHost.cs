using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Atli.Reports.Server, through its hosting integration (src/Atli.Reports.Aspire.Hosting). On its own,
// AddReportsServer pulls the image released with the package; WithDockerfile builds this checkout's
// server instead, with the repository root as the build context. It renders with its own
// chrome-headless-shell.
var reportsServer = builder
  .AddReportsServer("reports-server")
  .WithDevelopmentApiKey()
  .WithDockerfile("../..", "src/Atli.Reports.Server/Dockerfile");

// The remote example renders its Blazor reports in-process and converts them on the reports server,
// through Atli.Reports.Client: it needs no browser on this machine. WithReference passes the server's
// address as ConnectionStrings__reports-server, and its /health includes the client's probe of the
// server, so it turns healthy once both are ready.
builder
  .AddProject<Projects.RemoteReportServer>("remote-report-server")
  .WithReference(reportsServer)
  .WaitFor(reportsServer)
  .WithHttpHealthCheck("/health");

// The examples run the engine in-process, so they need a browser on this machine. The engine finds
// Chrome or Chromium in the standard install locations. To use another browser, for example
// chrome-headless-shell, set ReportsEngine:Browser:ExecutablePath in the AppHost's configuration
// (user secrets, appsettings.json, or the ReportsEngine__Browser__ExecutablePath environment variable).
var browserExecutablePath = builder.Configuration["ReportsEngine:Browser:ExecutablePath"];

var simpleReportServer = builder
  .AddProject<Projects.SimpleReportServer>("simple-report-server")
  .WithHttpHealthCheck("/health")
  .WithUrlForEndpoint(
    "http",
    _ => new() { Url = "/openapi/v1.json", DisplayText = "OpenAPI document" }
  );

// The Tailwind package compiles and copies each report's stylesheet during the project build.
var tailwindReportServer = builder
  .AddProject<Projects.TailwindReportServer>("tailwind-report-server")
  .WithHttpHealthCheck("/health")
  .WithUrlForEndpoint(
    "http",
    _ => new() { Url = "/openapi/v1.json", DisplayText = "OpenAPI document" }
  );

if (!string.IsNullOrWhiteSpace(browserExecutablePath))
{
  foreach (var example in new[] { simpleReportServer, tailwindReportServer })
  {
    example.WithEnvironment("ReportsEngine__Browser__ExecutablePath", browserExecutablePath);
  }
}

// Gotenberg, for side-by-side comparisons with the reports server. Off by default; turn it on with
// Gotenberg:Enabled=true in the AppHost's configuration.
if (builder.Configuration.GetValue<bool>("Gotenberg:Enabled"))
{
  builder
    .AddContainer("gotenberg", "gotenberg/gotenberg", "8.37.0-chromium")
    .WithHttpEndpoint(targetPort: 3000)
    .WithHttpHealthCheck("/health");
}

builder.Build().Run();
