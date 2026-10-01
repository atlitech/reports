using System.Text.Json;
using Atli.Reports.Server.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Server.Health;

public static class HealthCheckResponseWriter
{
  public static Task WriteResponse(HttpContext context, HealthReport report)
  {
    context.Response.ContentType = "application/json";

    Dictionary<string, HealthCheckEntry> checks = [];

    foreach (var entry in report.Entries)
    {
      checks[entry.Key] = new HealthCheckEntry
      {
        Status = entry.Value.Status.ToString(),
        Description = entry.Value.Description,
      };
    }

    HealthCheckResponse response = new() { Status = report.Status.ToString(), Checks = checks };

    return JsonSerializer.SerializeAsync(
      context.Response.Body,
      response,
      ServerJsonSerializerContext.Default.HealthCheckResponse
    );
  }
}

public sealed class HealthCheckResponse
{
  public required string Status { get; init; }
  public required Dictionary<string, HealthCheckEntry> Checks { get; init; }
}

public sealed class HealthCheckEntry
{
  public required string Status { get; init; }
  public string? Description { get; init; }
}
