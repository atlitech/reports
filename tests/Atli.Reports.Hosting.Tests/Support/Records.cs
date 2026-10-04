using System.Text.Json.Nodes;
using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Hosting.Tests.Support;

/// <summary>Renderer records for tests.</summary>
internal static class Records
{
  public static RendererRecord Acme() =>
    new()
    {
      TenantId = "acme",
      Url = new Uri("https://acme--8080.eastus2.adcproxy.io/"),
      ApiKey = "reports-0123456789ab." + new string('f', 64),
      SandboxId = "98c01b65-b81b-4dca-b000-fdae0eb0939c",
      DiskImageId = "c3d87d13-9ce3-4fb5-b0db-db168ea50aa6",
      CreatedAt = new DateTimeOffset(2026, 10, 4, 1, 50, 55, TimeSpan.Zero),
      MaxConcurrentRequests = 2,
    };

  /// <summary>The record as a store writes it, for seeding a store directly.</summary>
  public static string Json(RendererRecord record) =>
    new JsonObject
    {
      ["tenantId"] = record.TenantId,
      ["url"] = record.Url.AbsoluteUri,
      ["apiKey"] = record.ApiKey,
      ["sandboxId"] = record.SandboxId,
      ["diskImageId"] = record.DiskImageId,
      ["createdAt"] = record.CreatedAt,
      ["maxConcurrentRequests"] = record.MaxConcurrentRequests,
    }.ToJsonString();
}
