using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// The Aspire service defaults for the example apps: OpenTelemetry, health checks, service discovery,
/// and resilient HTTP clients. See https://aka.ms/dotnet/aspire/service-defaults.
/// </summary>
public static class Extensions
{
  private const string HealthEndpointPath = "/health";
  private const string AlivenessEndpointPath = "/alive";

  /// <summary>
  /// The name of the reports engine's meter and activity source.
  /// </summary>
  private const string ReportsEngineTelemetryName = "Atli.Reports.Engine";

  /// <summary>
  /// The name of the Blazor reports activity source, which traces component rendering.
  /// </summary>
  private const string BlazorReportTelemetryName = "Atli.Reports.Blazor";

  /// <summary>
  /// The .NET runtime's built-in meter (GC, thread pool, JIT, exceptions). Since .NET 9 the runtime
  /// publishes these itself, so no instrumentation package is needed.
  /// </summary>
  private const string RuntimeMeterName = "System.Runtime";

  /// <summary>
  /// Adds OpenTelemetry, the default health checks, service discovery, and the standard resilience
  /// handler for every <see cref="HttpClient"/>.
  /// </summary>
  public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
    where TBuilder : IHostApplicationBuilder
  {
    builder.ConfigureOpenTelemetry();

    builder.AddDefaultHealthChecks();

    builder.Services.AddServiceDiscovery();

    builder.Services.ConfigureHttpClientDefaults(http =>
    {
      http.AddStandardResilienceHandler();
      http.AddServiceDiscovery();
    });

    return builder;
  }

  /// <summary>
  /// Collects logs, metrics, and traces, including the reports engine's, and exports them over OTLP
  /// when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, as it is under the AppHost.
  /// </summary>
  public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
    where TBuilder : IHostApplicationBuilder
  {
    builder.Logging.AddOpenTelemetry(logging =>
    {
      logging.IncludeFormattedMessage = true;
      logging.IncludeScopes = true;
    });

    builder
      .Services.AddOpenTelemetry()
      .WithMetrics(metrics =>
      {
        metrics
          .AddAspNetCoreInstrumentation()
          .AddHttpClientInstrumentation()
          .AddMeter(RuntimeMeterName)
          .AddMeter(ReportsEngineTelemetryName);
      })
      .WithTracing(tracing =>
      {
        tracing
          .AddSource(builder.Environment.ApplicationName)
          .AddSource(ReportsEngineTelemetryName)
          .AddSource(BlazorReportTelemetryName)
          .AddAspNetCoreInstrumentation(options =>
            // Health probes would drown out the requests worth looking at.
            options.Filter = context =>
              !context.Request.Path.StartsWithSegments(HealthEndpointPath)
              && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
          )
          .AddHttpClientInstrumentation();
      });

    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    {
      builder.Services.AddOpenTelemetry().UseOtlpExporter();
    }

    return builder;
  }

  /// <summary>
  /// Adds a liveness check that reports healthy whenever the app responds.
  /// </summary>
  public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
    where TBuilder : IHostApplicationBuilder
  {
    builder
      .Services.AddHealthChecks()
      .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

    return builder;
  }

  /// <summary>
  /// Maps <c>/health</c> (every check must pass) and <c>/alive</c> (the checks tagged <c>live</c>), in
  /// every environment. Probe responses contain only a status, not dependency diagnostics.
  /// </summary>
  /// <remarks>
  /// Restrict these endpoints to the deployment's probe network at ingress.
  /// </remarks>
  public static WebApplication MapDefaultEndpoints(this WebApplication app)
  {
    app.MapHealthChecks(HealthEndpointPath);
    app.MapHealthChecks(
      AlivenessEndpointPath,
      new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") }
    );

    return app;
  }
}
