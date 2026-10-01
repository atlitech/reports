using Atli.Reports.Engine;
using Atli.Reports.Server.Endpoints;
using Atli.Reports.Server.Health;
using Atli.Reports.Server.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Register the reports engine; all configuration comes from appsettings / environment variables.
builder.Services.AddReportsEngine(
  builder.Configuration.GetSection(ReportsEngineOptions.SectionName)
);

builder.Services.ConfigureHttpJsonOptions(options =>
{
  options.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonSerializerContext.Default);
});

builder
  .Services.AddHealthChecks()
  .AddReportsEngineBrowserCheck("browser_executable", tags: ["live", "ready"])
  .AddReportsEngineConversionCheck("conversion_health", tags: ["ready"]);

var app = builder.Build();

app.MapConvertEndpoints();

app.MapHealthChecks(
  "/health/live",
  new HealthCheckOptions
  {
    Predicate = r => r.Tags.Contains("live"),
    ResponseWriter = HealthCheckResponseWriter.WriteResponse,
  }
);

app.MapHealthChecks(
  "/health/ready",
  new HealthCheckOptions
  {
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteResponse,
  }
);

app.Run();
