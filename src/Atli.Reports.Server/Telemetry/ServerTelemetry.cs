using Atli.Reports.Engine;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Atli.Reports.Server.Telemetry;

/// <summary>
/// Exports the server's logs, metrics, and traces over OTLP when an OTLP endpoint is configured.
/// </summary>
/// <remarks>
/// <para>
/// Everything follows the standard OpenTelemetry environment variables, which also bind from any
/// other configuration source (appsettings, the command line):
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> switches the export on (without it, OpenTelemetry is not
/// registered at all); <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>, <c>OTEL_EXPORTER_OTLP_HEADERS</c>, and the
/// signal-specific variables configure the exporter; <c>OTEL_SERVICE_NAME</c> and
/// <c>OTEL_RESOURCE_ATTRIBUTES</c> override the default service name, <see cref="ServiceName"/>.
/// .NET Aspire sets all of them for the resources it runs.
/// </para>
/// <para>
/// Traces: incoming requests (except the <c>/health</c> endpoints) and the engine's conversion
/// spans. Metrics: ASP.NET Core, the .NET runtime, and the engine. Logs: everything the logging
/// configuration lets through.
/// </para>
/// </remarks>
internal static class ServerTelemetry
{
  /// <summary>
  /// The service name used unless <c>OTEL_SERVICE_NAME</c> or <c>OTEL_RESOURCE_ATTRIBUTES</c> sets
  /// one.
  /// </summary>
  public const string ServiceName = "atli-reports-server";

  /// <summary>
  /// The configuration key that switches the export on.
  /// </summary>
  public const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

  /// <summary>
  /// The .NET runtime's built-in meter (GC, thread pool, JIT, exceptions). Since .NET 9 the runtime
  /// publishes these itself, so no instrumentation package is needed.
  /// </summary>
  private const string RuntimeMeterName = "System.Runtime";

  /// <summary>
  /// Registers OpenTelemetry with an OTLP exporter, if <see cref="EndpointKey"/> is configured.
  /// </summary>
  public static WebApplicationBuilder AddServerTelemetry(this WebApplicationBuilder builder)
  {
    if (string.IsNullOrWhiteSpace(builder.Configuration[EndpointKey]))
    {
      return builder;
    }

    builder
      .Services.AddOpenTelemetry()
      .ConfigureResource(resource =>
        resource
          .AddService(
            ServiceName,
            serviceVersion: typeof(ServerTelemetry).Assembly.GetName().Version?.ToString(3)
          )
          // Detected again after the defaults above, so OTEL_SERVICE_NAME and
          // OTEL_RESOURCE_ATTRIBUTES take precedence over them.
          .AddEnvironmentVariableDetector()
      )
      .WithTracing(tracing =>
        tracing
          .AddSource(ReportsEngineTelemetry.ActivitySourceName)
          .AddAspNetCoreInstrumentation(options =>
            // Probes poll these every few seconds; their traces would drown the conversions.
            options.Filter = context => !context.Request.Path.StartsWithSegments("/health")
          )
      )
      .WithMetrics(metrics =>
        metrics
          .AddMeter(ReportsEngineTelemetry.MeterName)
          .AddAspNetCoreInstrumentation()
          .AddMeter(RuntimeMeterName)
      )
      .WithLogging(
        configureBuilder: null,
        configureOptions: options =>
        {
          options.IncludeFormattedMessage = true;
          options.IncludeScopes = true;
        }
      )
      .UseOtlpExporter();

    return builder;
  }
}
