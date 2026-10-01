using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Atli.Reports.Benchmarks.Shared;

namespace Atli.Reports.Benchmarks.Load.Targets;

/// <summary>
/// A request body prepared once per fixture and replayed for every request.
/// </summary>
internal sealed record PreparedRequest(string Path, byte[] Body, string ContentType)
{
  public HttpRequestMessage CreateMessage(Uri baseAddress)
  {
    ByteArrayContent content = new(Body);
    content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);
    return new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, Path))
    {
      Content = content,
    };
  }
}

/// <summary>
/// A converter under test: the compose service that runs it and how to ask it for a PDF.
/// </summary>
/// <remarks>
/// Both targets receive the same page settings — A4, 0.4 inch margins, background graphics, tagged
/// PDF — so they print the same document. Each sets explicitly what the other does by default.
/// </remarks>
internal abstract class BenchmarkTarget(
  string name,
  string service,
  Uri baseAddress,
  string healthPath
)
{
  public string Name => name;

  public string Service => service;

  public Uri BaseAddress => baseAddress;

  public Uri HealthUri => new(baseAddress, healthPath);

  /// <summary>
  /// The command that prints the browser version inside the container.
  /// </summary>
  public abstract string BrowserVersionCommand { get; }

  public abstract Task<PreparedRequest> PrepareAsync(BenchmarkFixture fixture, string html);

  public static BenchmarkTarget Create(string name, int port) =>
    name.ToLowerInvariant() switch
    {
      "atli" => new AtliTarget(port),
      "gotenberg" => new GotenbergTarget(port),
      _ => throw new ArgumentException($"Unknown target '{name}'. Known targets: atli, gotenberg."),
    };
}

/// <summary>
/// Atli.Reports.Server: <c>POST /convert</c> with a JSON body.
/// </summary>
internal sealed class AtliTarget(int port)
  : BenchmarkTarget("atli", "atli", new Uri($"http://localhost:{port}/"), "health/ready")
{
  public override string BrowserVersionCommand => "/usr/bin/chromium --version";

  public override Task<PreparedRequest> PrepareAsync(BenchmarkFixture fixture, string html)
  {
    using MemoryStream body = new();
    using (Utf8JsonWriter json = new(body))
    {
      json.WriteStartObject();
      json.WriteString("html", html);
      json.WriteStartObject("options");
      json.WriteString("paperSize", "a4");
      json.WriteStartObject("margins");
      json.WriteNumber("top", BenchmarkFixtures.MarginInches);
      json.WriteNumber("bottom", BenchmarkFixtures.MarginInches);
      json.WriteNumber("left", BenchmarkFixtures.MarginInches);
      json.WriteNumber("right", BenchmarkFixtures.MarginInches);
      json.WriteEndObject();
      json.WriteBoolean("printBackground", true);
      if (fixture.WaitsForSignal)
      {
        json.WriteString("waitForSignal", BenchmarkFixtures.SignalName);
        json.WriteNumber("waitTimeoutSeconds", BenchmarkFixtures.SignalTimeoutSeconds);
      }

      json.WriteEndObject();
      json.WriteEndObject();
    }

    return Task.FromResult(new PreparedRequest("convert", body.ToArray(), "application/json"));
  }
}

/// <summary>
/// Gotenberg 8: <c>POST /forms/chromium/convert/html</c> with a multipart form.
/// </summary>
internal sealed class GotenbergTarget(int port)
  : BenchmarkTarget("gotenberg", "gotenberg", new Uri($"http://localhost:{port}/"), "health")
{
  public override string BrowserVersionCommand => "chromium --version";

  public override async Task<PreparedRequest> PrepareAsync(BenchmarkFixture fixture, string html)
  {
    using MultipartFormDataContent form = new("atli-reports-bench-boundary");
    StringContent file = new(html);
    file.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
    form.Add(file, "files", "index.html");

    // Gotenberg's defaults differ from Atli.Reports' (Letter, 0.39 in margins, no backgrounds, untagged
    // PDF); these match the document Atli.Reports prints.
    AddField(form, "paperWidth", BenchmarkFixtures.PaperWidthInches);
    AddField(form, "paperHeight", BenchmarkFixtures.PaperHeightInches);
    AddField(form, "marginTop", BenchmarkFixtures.MarginInches);
    AddField(form, "marginBottom", BenchmarkFixtures.MarginInches);
    AddField(form, "marginLeft", BenchmarkFixtures.MarginInches);
    AddField(form, "marginRight", BenchmarkFixtures.MarginInches);
    form.Add(new StringContent("true"), "printBackground");
    form.Add(new StringContent("true"), "generateTaggedPdf");
    if (fixture.WaitsForSignal)
    {
      form.Add(new StringContent(BenchmarkFixtures.GotenbergReadyExpression), "waitForExpression");
    }

    var body = await form.ReadAsByteArrayAsync();
    var contentType =
      form.Headers.ContentType?.ToString()
      ?? throw new InvalidOperationException("The multipart form has no content type.");
    return new PreparedRequest("forms/chromium/convert/html", body, contentType);
  }

  private static void AddField(MultipartFormDataContent form, string name, double value) =>
    form.Add(new StringContent(value.ToString(CultureInfo.InvariantCulture)), name);
}
