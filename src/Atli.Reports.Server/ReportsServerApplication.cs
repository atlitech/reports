using Atli.Reports.Engine;
using Atli.Reports.Server.Endpoints;
using Atli.Reports.Server.Health;
using Atli.Reports.Server.OpenApi;
using Atli.Reports.Server.Serialization;
using Atli.Reports.Server.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Atli.Reports.Server;

/// <summary>
/// Builds the reports server: the <c>/convert</c> endpoint and the health endpoints over the reports
/// engine, the OpenAPI document that describes <c>/convert</c>, and the OTLP export of its telemetry
/// when an endpoint is configured.
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

    // Kestrel binds its endpoints from the Kestrel section but not its limits, so
    // Kestrel__Limits__MaxRequestBodySize and the other KestrelServerLimits need binding here.
    builder.WebHost.ConfigureKestrel(
      (context, kestrel) => context.Configuration.GetSection("Kestrel:Limits").Bind(kestrel.Limits)
    );

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
      options.SerializerOptions.TypeInfoResolverChain.Insert(
        0,
        ServerJsonSerializerContext.Default
      );
      // A repeated property is ambiguous (which html is meant?), so it is a bad request rather than
      // last-one-wins. The source-generated context's options do not govern request bodies; these do.
      options.SerializerOptions.AllowDuplicateProperties = false;
    });

    // Every error response is problem details with a kind, including the 400, 413, and 415 that
    // request binding sets before the endpoint runs and the 500 of an unhandled exception.
    builder.Services.AddProblemDetails(options =>
      options.CustomizeProblemDetails = ConversionProblems.Customize
    );
    builder.Services.AddExceptionHandler(options =>
      // In Development, request binding throws its error status in a BadHttpRequestException.
      options.StatusCodeSelector = exception =>
        exception is BadHttpRequestException badRequest
          ? badRequest.StatusCode
          : StatusCodes.Status500InternalServerError
    );

    // Both checks gate readiness only. A browser that cannot start takes the server out of rotation
    // while the engine retries the launch in the background; restarting the process would not repair
    // it, so liveness asks no more than that the server answers.
    builder
      .Services.AddHealthChecks()
      .AddReportsEngineBrowserCheck("browser", tags: ["ready"])
      .AddReportsEngineConversionCheck("conversion_health", tags: ["ready"]);

    builder.AddServerTelemetry();

    builder.Services.AddServerOpenApi();

    configure?.Invoke(builder);

    var app = builder.Build();

    // A response that already started (a PDF partway through) is not rewritten: the exception
    // handler rethrows, and Kestrel aborts the connection.
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    app.MapConvertEndpoints();

    // The document is the server's public contract, so every environment serves it.
    app.MapOpenApi();

    // Probes poll these every few seconds; like their traces, their request metrics would drown the
    // conversions. They are operational, so the OpenAPI document leaves them out.
    app.MapHealthChecks(
        "/health/live",
        new HealthCheckOptions
        {
          Predicate = r => r.Tags.Contains("live"),
          ResponseWriter = HealthCheckResponseWriter.WriteResponse,
        }
      )
      .DisableHttpMetrics()
      .ExcludeFromDescription();

    app.MapHealthChecks(
        "/health/ready",
        new HealthCheckOptions
        {
          Predicate = r => r.Tags.Contains("ready"),
          ResponseWriter = HealthCheckResponseWriter.WriteResponse,
        }
      )
      .DisableHttpMetrics()
      .ExcludeFromDescription();

    return app;
  }
}
