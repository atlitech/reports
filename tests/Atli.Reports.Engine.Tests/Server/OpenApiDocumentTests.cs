using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Server;
using Atli.Reports.Server.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Atli.Reports.Engine.Tests.Server;

/// <summary>
/// The server's OpenAPI document at <c>/openapi/v1.json</c>, its public contract.
/// </summary>
public class OpenApiDocumentTests
{
  private const string DocumentPath = "/openapi/v1.json";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Production_serves_an_openapi_3_1_document_named_and_versioned_after_the_server()
  {
    await using var server = await RunningServer.StartAsync(
      converter: null,
      "--environment=Production"
    );

    using var response = await server.Client.GetAsync(DocumentPath, TestToken);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(response.Content.Headers.ContentType?.MediaType)
      .IsEqualTo("application/json");
    var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestToken))!.AsObject();
    await Assert.That(Text(document["openapi"])).StartsWith("3.1.");
    await Assert.That(Text(document["info"]!["title"])).IsEqualTo("Atli Reports Server");
    var release = typeof(ReportsServerApplication).Assembly.GetName().Version!.ToString(3);
    await Assert
      .That(Text(document["info"]!["version"]))
      .StartsWith(release)
      .And.DoesNotContain("+")
      .Because("the version is the server's release, without build metadata");
  }

  [Test]
  public async Task Only_convert_is_described_not_the_health_endpoints()
  {
    await using var server = await RunningServer.StartAsync(converter: null);

    var document = await GetDocumentAsync(server);

    await Assert
      .That(document["paths"]!.AsObject().Select(path => path.Key))
      .IsEquivalentTo(["/convert"]);
    var operation = Operation(document);
    await Assert.That(Text(operation["operationId"])).IsEqualTo("Convert");
    await Assert.That(Text(operation["summary"])).IsNotEmpty();
    await Assert.That(Text(operation["description"])).IsNotEmpty();
  }

  [Test]
  public async Task Convert_takes_a_json_conversion_request_described_by_its_xml_comments()
  {
    await using var server = await RunningServer.StartAsync(converter: null);

    var document = await GetDocumentAsync(server);

    var requestBody = Operation(document)["requestBody"]!;
    await Assert.That(requestBody["required"]!.GetValue<bool>()).IsTrue();
    var content = requestBody["content"]!.AsObject();
    await Assert.That(content.Select(entry => entry.Key)).IsEquivalentTo(["application/json"]);
    var request = Resolve(document, content["application/json"]!["schema"]!);
    await Assert.That(Texts(request["required"])).IsEquivalentTo(["html"]);
    await Assert.That(Text(request["properties"]!["html"]!["type"])).IsEqualTo("string");

    var options = Resolve(document, request["properties"]!["options"]!);
    await Assert
      .That(options["properties"]!.AsObject().Select(property => property.Key))
      .IsEquivalentTo([
        "orientation",
        "paperSize",
        "paperWidth",
        "paperHeight",
        "margins",
        "printBackground",
        "scale",
        "headerTemplate",
        "footerTemplate",
        "displayHeaderFooter",
        "pageRanges",
        "preferCSSPageSize",
        "generateTaggedPdf",
        "waitForSignal",
        "waitTimeoutSeconds",
      ]);
    var margins = Resolve(document, options["properties"]!["margins"]!);
    foreach (var schema in new[] { request, options, margins })
    {
      await Assert.That(Text(schema["description"])).IsNotEmpty();
      foreach (var (name, property) in schema["properties"]!.AsObject())
      {
        // A property that refers to another schema carries its description beside the reference.
        var description =
          property!["description"]
          ?? property["oneOf"]
            ?.AsArray()
            .Select(option => option!["description"])
            .Single(d => d is not null);
        await Assert.That(Text(description)).IsNotEmpty().Because($"{name} has an XML comment");
      }
    }
  }

  [Test]
  public async Task Options_with_a_fixed_vocabulary_list_their_values_and_the_server_accepts_each()
  {
    FakeConverter converter = new((_, _) => Task.FromResult<ConversionError?>(null));
    await using var server = await RunningServer.StartAsync(converter);

    var document = await GetDocumentAsync(server);

    var options = document["components"]!["schemas"]!["PdfOptionsRequest"]!["properties"]!;
    await Assert
      .That(Values(options["orientation"]))
      .IsEquivalentTo(["portrait", "landscape", null]);
    await Assert
      .That(Values(options["paperSize"]))
      .IsEquivalentTo(["letter", "legal", "a4", "a3", null]);
    foreach (var name in new[] { "orientation", "paperSize" })
    {
      foreach (var value in options[name]!["enum"]!.AsArray())
      {
        JsonObject body = new()
        {
          ["html"] = "<p>x</p>",
          ["options"] = new JsonObject { [name] = value?.DeepClone() },
        };
        using var response = await server.PostAsync(body.ToJsonString());
        await Assert
          .That(response.StatusCode)
          .IsEqualTo(HttpStatusCode.OK)
          .Because($"{name} {value?.ToJsonString() ?? "null"} is in the document");
      }
    }
  }

  [Test]
  public async Task Convert_answers_a_binary_pdf()
  {
    await using var server = await RunningServer.StartAsync(converter: null);

    var document = await GetDocumentAsync(server);

    var content = Operation(document)["responses"]!["200"]!["content"]!.AsObject();
    await Assert.That(content.Select(entry => entry.Key)).IsEquivalentTo(["application/pdf"]);
    var schema = Resolve(document, content["application/pdf"]!["schema"]!);
    await Assert.That(Text(schema["type"])).IsEqualTo("string");
    await Assert.That(Text(schema["format"])).IsEqualTo("binary");
  }

  [Test]
  public async Task Every_error_status_is_problem_details_with_a_kind_the_description_names()
  {
    await using var server = await RunningServer.StartAsync(converter: null);

    var document = await GetDocumentAsync(server);

    // Every status a conversion error is written with (a canceled request's 499 reaches no client),
    // plus body-binding failures and the per-caller 429, with the kinds each status carries.
    var kindsByStatus = Enum.GetValues<ConversionErrorKind>()
      .Where(kind => kind != ConversionErrorKind.Canceled)
      .Select(kind => (Status: ConversionProblems.StatusCode(kind), Kind: kind))
      .Concat(
        new[]
        {
          StatusCodes.Status413PayloadTooLarge,
          StatusCodes.Status415UnsupportedMediaType,
          StatusCodes.Status429TooManyRequests,
        }.Select(status => (Status: status, Kind: ConversionProblems.KindForStatus(status)))
      )
      .ToLookup(entry => entry.Status.ToString(CultureInfo.InvariantCulture), entry => entry.Kind);
    var responses = Operation(document)["responses"]!.AsObject();
    await Assert
      .That(responses.Select(response => response.Key))
      .IsEquivalentTo(["200", .. kindsByStatus.Select(group => group.Key)]);

    foreach (var kinds in kindsByStatus)
    {
      var response = responses[kinds.Key]!;
      foreach (var kindOfStatus in kinds)
      {
        await Assert.That(Text(response["description"])).Contains($"`{kindOfStatus}`");
      }

      var content = response["content"]!.AsObject();
      await Assert
        .That(content.Select(entry => entry.Key))
        .IsEquivalentTo(["application/problem+json"]);
      var problem = Resolve(document, content["application/problem+json"]!["schema"]!);
      await Assert.That(Texts(problem["required"])).Contains("kind");
      var kind = problem["properties"]!["kind"]!;
      await Assert.That(Text(kind["type"])).IsEqualTo("string");
      await Assert.That(Texts(kind["enum"])).IsEquivalentTo(Enum.GetNames<ConversionErrorKind>());
    }

    foreach (var status in new[] { "429", "503" })
    {
      var retryAfter = responses[status]!["headers"]!["Retry-After"]!;
      await Assert.That(Text(retryAfter["schema"]!["type"])).IsEqualTo("integer");
    }
  }

  private static async Task<JsonObject> GetDocumentAsync(RunningServer server)
  {
    var json = await server.Client.GetStringAsync(DocumentPath, TestToken);
    return JsonNode.Parse(json)!.AsObject();
  }

  private static JsonNode Operation(JsonObject document) =>
    document["paths"]!["/convert"]!["post"]!;

  /// <summary>
  /// Follows a <c>$ref</c>, including that of a nullable reference (<c>oneOf</c> null or the
  /// reference), to the schema it names in the document's components.
  /// </summary>
  private static JsonNode Resolve(JsonObject document, JsonNode schema)
  {
    const string Prefix = "#/components/schemas/";
    var reference =
      schema["$ref"]
      ?? schema["oneOf"]?.AsArray().Select(option => option!["$ref"]).Single(r => r is not null);
    return reference is null
      ? schema
      : document["components"]!["schemas"]![Text(reference)[Prefix.Length..]]!;
  }

  private static string Text(JsonNode? node) => node!.GetValue<string>();

  private static IEnumerable<string> Texts(JsonNode? node) => node!.AsArray().Select(Text);

  /// <summary>
  /// The values of the <c>enum</c> in <paramref name="schema"/>, null included.
  /// </summary>
  private static IEnumerable<string?> Values(JsonNode? schema) =>
    schema!["enum"]!.AsArray().Select(value => value?.GetValue<string>());
}
