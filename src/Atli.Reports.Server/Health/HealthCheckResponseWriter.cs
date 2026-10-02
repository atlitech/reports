using Atli.Reports.Server.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Server.Health;

/// <summary>
/// Writes a health report as JSON: the overall status and each check's status and description.
/// </summary>
public static class HealthCheckResponseWriter
{
  /// <summary>Writes only the aggregate status for anonymous platform probes.</summary>
  public static Task WriteSummary(HttpContext context, HealthReport report) =>
    context.Response.WriteAsJsonAsync(
      new HealthSummaryResponse(report.Status.ToString()),
      ServerJsonSerializerContext.Default.HealthSummaryResponse,
      cancellationToken: context.RequestAborted
    );

  /// <summary>
  /// Writes <paramref name="report"/> as the response.
  /// </summary>
  /// <param name="context">The request's context.</param>
  /// <param name="report">The health report.</param>
  public static Task WriteResponse(HttpContext context, HealthReport report)
  {
    Dictionary<string, HealthCheckEntry> checks = [];

    foreach (var entry in report.Entries)
    {
      checks[entry.Key] = new HealthCheckEntry(
        entry.Value.Status.ToString(),
        entry.Value.Description
      );
    }

    return context.Response.WriteAsJsonAsync(
      new HealthCheckResponse(report.Status.ToString(), checks),
      ServerJsonSerializerContext.Default.HealthCheckResponse,
      cancellationToken: context.RequestAborted
    );
  }
}

internal sealed record HealthCheckResponse(
  string Status,
  Dictionary<string, HealthCheckEntry> Checks
);

internal sealed record HealthCheckEntry(string Status, string? Description);

internal sealed record HealthSummaryResponse(string Status);
