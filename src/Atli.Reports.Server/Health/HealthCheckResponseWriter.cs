using Atli.Reports.Server.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Server.Health;

public static class HealthCheckResponseWriter
{
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
