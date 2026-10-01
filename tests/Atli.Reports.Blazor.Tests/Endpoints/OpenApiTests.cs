using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Endpoints;

/// <summary>
/// How Microsoft.AspNetCore.OpenApi describes the endpoints <c>MapBlazorReport</c> maps.
/// </summary>
public class OpenApiTests
{
  [Test]
  [Arguments("/greetingreport", "application/pdf")]
  [Arguments("/greetinghtml", "text/html")]
  public async Task A_report_is_described_as_binary_content_of_its_format(
    string path,
    string contentType
  )
  {
    await using var server = await TestReportServer.StartAsync(
      app =>
      {
        app.MapBlazorReport<GreetingReport, GreetingData>();
        app.MapBlazorReport<GreetingReport, GreetingData>(options =>
        {
          options.ReportName = "GreetingHtml";
          options.OutputFormat = ReportOutputFormat.Html;
        });
        app.MapOpenApi();
      },
      configureServices: services =>
      {
        services.AddSingleton<IHtmlToPdfConverter>(FakeHtmlToPdfConverter.Succeeding());
        services.AddOpenApi();
      }
    );

    var document = await server.Client.GetFromJsonAsync<JsonObject>(
      "/openapi/v1.json",
      TestContext.Current!.Execution.CancellationToken
    );

    var content = document!["paths"]![path]!["post"]!["responses"]!["200"]!["content"]!.AsObject();
    await Assert.That(content.Select(entry => entry.Key)).IsEquivalentTo([contentType]);
    var schema = Resolve(document, content[contentType]!["schema"]!);
    await Assert.That(schema["type"]?.GetValue<string>()).IsEqualTo("string");
    await Assert.That(schema["format"]?.GetValue<string>()).IsEqualTo("binary");
  }

  /// <summary>
  /// Follows a <c>$ref</c> to the schema it names in the document's components.
  /// </summary>
  private static JsonNode Resolve(JsonObject document, JsonNode schema)
  {
    const string Prefix = "#/components/schemas/";
    return schema["$ref"]?.GetValue<string>() is { } reference
      ? document["components"]!["schemas"]![reference[Prefix.Length..]]!
      : schema;
  }
}
