using Atli.Reports.Engine;
using Atli.Reports.Server.Endpoints;
using Atli.Reports.Server.Health;
using Atli.Reports.Server.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Atli.Reports.Server;

/// <summary>
/// Builds the reports server: the <c>/convert</c> endpoint and the health endpoints over the reports
/// engine.
/// </summary>
public static class ReportsServerApplication
{
  /// <summary>
  /// Creates the application. All configuration comes from the usual sources (appsettings, environment
  /// variables such as <c>ReportsEngine__Concurrency__MaxConcurrentConversions</c>, and the command line).
  /// </summary>
  /// <param name="args">The command-line arguments.</param>
  /// <param name="configure">Adjusts the builder before the application is built; tests use it to replace services.</param>
  public static WebApplication Create(
    string[] args,
    Action<WebApplicationBuilder>? configure = null
  )
  {
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddReportsEngine(
      builder.Configuration.GetSection(ReportsEngineOptions.SectionName)
    );

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
      options.SerializerOptions.TypeInfoResolverChain.Insert(
        0,
        ServerJsonSerializerContext.Default
      );
    });
    builder.Services.AddProblemDetails();

    builder
      .Services.AddHealthChecks()
      .AddReportsEngineBrowserCheck("browser_executable", tags: ["live", "ready"])
      .AddReportsEngineConversionCheck("conversion_health", tags: ["ready"]);

    configure?.Invoke(builder);

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

    return app;
  }
}
