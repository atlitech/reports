using System.Text.Json;
using System.Text.Json.Nodes;
using Atli.Reports.Client.Tests.Support;
using Atli.Reports.Engine;

namespace Atli.Reports.Client.Tests.Http;

/// <summary>
/// The JSON the client posts to the server's <c>/convert</c>.
/// </summary>
public class RequestBodyTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Every_option_is_spelled_out_in_the_servers_request_shape()
  {
    var body = await PostAsync(
      "<!DOCTYPE html><p>Hello</p>",
      new PdfOptions
      {
        Orientation = PageOrientation.Landscape,
        PaperSize = PaperSize.A4,
        Margins = new Margins
        {
          Top = 1,
          Bottom = 0.5,
          Left = 0.25,
          Right = 0,
        },
        PrintBackground = false,
        Scale = 1.5,
        HeaderTemplate = "<span class=\"title\"></span>",
        FooterTemplate = "<span class=\"pageNumber\"></span>",
        DisplayHeaderFooter = true,
        PageRanges = "1-2, 4",
        PreferCssPageSize = true,
        GenerateTaggedPdf = true,
        WaitForSignal = "pdfReady",
        WaitTimeout = TimeSpan.FromSeconds(2.5),
      }
    );

    var expected = JsonNode.Parse(
      """
      {
        "html": "<!DOCTYPE html><p>Hello</p>",
        "options": {
          "orientation": "landscape",
          "paperSize": "a4",
          "margins": { "top": 1, "bottom": 0.5, "left": 0.25, "right": 0 },
          "printBackground": false,
          "scale": 1.5,
          "headerTemplate": "<span class=\"title\"></span>",
          "footerTemplate": "<span class=\"pageNumber\"></span>",
          "displayHeaderFooter": true,
          "pageRanges": "1-2, 4",
          "preferCSSPageSize": true,
          "generateTaggedPdf": true,
          "waitForSignal": "pdfReady",
          "waitTimeoutSeconds": 2.5
        }
      }
      """
    );
    await Assert.That(JsonNode.DeepEquals(body, expected)).IsTrue().Because(body!.ToJsonString());
  }

  [Test]
  public async Task Default_options_are_sent_explicitly_and_unset_ones_are_omitted()
  {
    var body = await PostAsync("<p>x</p>", options: null);

    var expected = JsonNode.Parse(
      """
      {
        "html": "<p>x</p>",
        "options": {
          "orientation": "portrait",
          "paperSize": "letter",
          "margins": { "top": 0.4, "bottom": 0.4, "left": 0.4, "right": 0.4 },
          "printBackground": true,
          "scale": 1,
          "displayHeaderFooter": false,
          "preferCSSPageSize": false,
          "waitTimeoutSeconds": 30
        }
      }
      """
    );
    await Assert.That(JsonNode.DeepEquals(body, expected)).IsTrue().Because(body!.ToJsonString());
  }

  [Test]
  [Arguments("letter", 8.5, 11)]
  [Arguments("legal", 8.5, 14)]
  [Arguments("a4", 8.27, 11.69)]
  [Arguments("a3", 11.69, 16.54)]
  public async Task Named_paper_sizes_travel_by_name(string name, double width, double height)
  {
    var body = await PostAsync(
      "<p>x</p>",
      new PdfOptions
      {
        PaperSize = new PaperSize { Width = width, Height = height },
      }
    );

    var options = body!["options"]!.AsObject();
    await Assert.That(options["paperSize"]!.GetValue<string>()).IsEqualTo(name);
    await Assert.That(options.ContainsKey("paperWidth")).IsFalse();
    await Assert.That(options.ContainsKey("paperHeight")).IsFalse();
  }

  [Test]
  public async Task A_custom_paper_size_travels_in_inches()
  {
    var body = await PostAsync(
      "<p>x</p>",
      new PdfOptions
      {
        PaperSize = new PaperSize { Width = 5.5, Height = 8.5 },
      }
    );

    var options = body!["options"]!.AsObject();
    await Assert.That(options.ContainsKey("paperSize")).IsFalse();
    await Assert.That(options["paperWidth"]!.GetValue<double>()).IsEqualTo(5.5);
    await Assert.That(options["paperHeight"]!.GetValue<double>()).IsEqualTo(8.5);
  }

  [Test]
  public async Task An_infinite_signal_wait_travels_as_minus_one_millisecond()
  {
    var body = await PostAsync(
      "<p>x</p>",
      new PdfOptions { WaitForSignal = "ready", WaitTimeout = Timeout.InfiniteTimeSpan }
    );

    await Assert
      .That(body!["options"]!["waitTimeoutSeconds"]!.GetValue<double>())
      .IsEqualTo(-0.001);
  }

  [Test]
  public async Task The_request_posts_json_to_convert_under_the_endpoint_path()
  {
    StubRequest? received = null;
    await using var server = StubServer.Start(
      (request, _) =>
      {
        received = request;
        return Task.FromResult(StubServer.Pdf("%PDF-1.7 %%EOF"u8.ToArray()));
      },
      settings => settings.Endpoint = new Uri("https://gateway.test/tenants/reports")
    );

    var result = await server.Converter.ConvertAsync("<p>x</p>", Stream.Null, null, TestToken);

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(received!.Method).IsEqualTo(HttpMethod.Post);
    await Assert
      .That(received.Uri)
      .IsEqualTo(new Uri("https://gateway.test/tenants/reports/convert"));
    await Assert.That(received.MediaType).IsEqualTo("application/json");
  }

  private static async Task<JsonNode?> PostAsync(string html, PdfOptions? options)
  {
    await using var server = StubServer.Start(
      (_, _) => Task.FromResult(StubServer.Pdf("%PDF-1.7 %%EOF"u8.ToArray()))
    );

    var result = await server.Converter.ConvertAsync(html, Stream.Null, options, TestToken);

    await Assert.That(result.IsT0).IsTrue();
    var request = server.Handler.Requests.Single();
    return JsonNode.Parse(request.Body, documentOptions: new JsonDocumentOptions());
  }
}
