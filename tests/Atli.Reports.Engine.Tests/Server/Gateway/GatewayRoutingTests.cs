using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Atli.Reports.Engine.Tests.Server.Gateway.GatewayHost;
using static Atli.Reports.Engine.Tests.Server.SecurityAuthenticationTests;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>
/// Gateway mode routes each conversion to the authenticated caller's tenant's renderer, with that
/// renderer's own credential and nothing else of the caller's request. The renderers here are the
/// real server in integrated mode, configured as the provisioner configures them.
/// </summary>
public class GatewayRoutingTests
{
  private const string AlphaKey = "alpha.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
  private const string BetaKey = "beta.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
  private const string GammaKey = "gamma.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  /// <summary>
  /// Three API-key callers: <c>alpha-app</c> in <c>acme</c>, <c>beta-app</c> in <c>acme</c> and
  /// <c>globex</c>, and <c>gamma-app</c> in none.
  /// </summary>
  private static string[] Callers() =>
    [
      "--ReportsServer:Authentication:Mode=ApiKey",
      .. KeySettings(0, "alpha", AlphaKey, caller: "alpha-app"),
      .. KeySettings(1, "beta", BetaKey, caller: "beta-app"),
      .. KeySettings(2, "gamma", GammaKey, caller: "gamma-app"),
      .. Membership(0, "alpha-app", "acme"),
      .. Membership(1, "beta-app", "acme", "globex"),
    ];

  [Test]
  public async Task Conversions_reach_the_tenants_renderer_with_that_renderers_own_key()
  {
    var acmeKey = RendererCredential.Generate();
    var globexKey = RendererCredential.Generate();
    var acmeConverter = PdfConverter("%PDF-1.7 acme");
    var globexConverter = PdfConverter("%PDF-1.7 globex");
    await using var acme = await StartRendererAsync(acmeConverter, acmeKey);
    await using var globex = await StartRendererAsync(globexConverter, globexKey);
    await using var gateway = await GatewayHost.StartAsync([
      .. Callers(),
      .. Renderer(0, "acme", acme.Client.BaseAddress!.ToString(), acmeKey.Credential),
      .. Renderer(1, "globex", globex.Client.BaseAddress!.ToString(), globexKey.Credential),
    ]);

    // One tenant: the header may be left out, or name that tenant.
    await AssertPdfAsync(await SendAsync(gateway, AlphaKey), "%PDF-1.7 acme");
    await AssertPdfAsync(await SendAsync(gateway, AlphaKey, "acme"), "%PDF-1.7 acme");
    // Several tenants: the header picks one of them.
    await AssertPdfAsync(await SendAsync(gateway, BetaKey, "globex"), "%PDF-1.7 globex");
    await AssertPdfAsync(await SendAsync(gateway, BetaKey, "acme"), "%PDF-1.7 acme");

    await Assert.That(acmeConverter.Calls).IsEqualTo(3);
    await Assert.That(globexConverter.Calls).IsEqualTo(1);
  }

  [Test]
  public async Task Tenant_membership_decides_and_the_header_only_selects()
  {
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    await using var gateway = await GatewayHost.StartAsync([
      .. Callers(),
      .. Renderer(0, "acme", renderer.BaseUrl, TestKey),
      .. Renderer(1, "globex", renderer.BaseUrl, TestKey),
      .. Renderer(2, "initech", renderer.BaseUrl, TestKey),
    ]);

    // No tenant at all: the authorization 403's shape.
    var none = await ReadProblemAsync(await SendAsync(gateway, GammaKey));
    await Assert.That(none.Status).IsEqualTo(403);
    await Assert.That(none.Kind).IsEqualTo("Forbidden");
    await Assert.That(none.Title).IsEqualTo("Forbidden");
    await Assert.That(none.Detail).Contains("any product tenant");

    // A single tenant's caller naming another tenant, even one with a renderer.
    var mismatch = await ReadProblemAsync(await SendAsync(gateway, AlphaKey, "initech"));
    await Assert.That(mismatch.Status).IsEqualTo(403);
    await Assert.That(mismatch.Kind).IsEqualTo("Forbidden");

    // Several tenants: the header is required, must be a member, and must be single.
    var missing = await ReadProblemAsync(await SendAsync(gateway, BetaKey));
    await Assert.That(missing.Status).IsEqualTo(400);
    await Assert.That(missing.Kind).IsEqualTo("InvalidRequest");
    await Assert.That(missing.Detail).Contains("X-Reports-Tenant");
    var nonMember = await ReadProblemAsync(await SendAsync(gateway, BetaKey, "initech"));
    await Assert.That(nonMember.Status).IsEqualTo(403);
    var malformed = await ReadProblemAsync(await SendAsync(gateway, BetaKey, "../ACME"));
    await Assert.That(malformed.Status).IsEqualTo(403);
    // HttpClient folds repeated values into one line, which is no tenant ID.
    var folded = await ReadProblemAsync(await SendAsync(gateway, BetaKey, "acme", "globex"));
    await Assert.That(folded.Status).IsEqualTo(403);
    // Two header lines are ambiguous, even when both name a member.
    await Assert
      .That(
        await SendRawAsync(gateway, BetaKey, "X-Reports-Tenant: acme", "X-Reports-Tenant: globex")
      )
      .StartsWith("HTTP/1.1 400 ");

    // The body never names the tenant.
    using var request = ConvertRequest("""{"html":"<p>x</p>","tenant":"initech"}""", GammaKey);
    using var bodyTenant = await gateway.Client.SendAsync(request, TestToken);
    await Assert.That(bodyTenant.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

    await Assert.That(renderer.Requests).IsEmpty();
  }

  [Test]
  public async Task An_empty_tenant_header_counts_as_absent()
  {
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7 acme")
    );
    await using var gateway = await GatewayHost.StartAsync([
      .. Callers(),
      .. Renderer(0, "acme", renderer.BaseUrl, TestKey),
      .. Renderer(1, "globex", renderer.BaseUrl, TestKey),
    ]);

    // One tenant: an empty header is no header.
    await AssertPdfAsync(await SendAsync(gateway, AlphaKey, ""), "%PDF-1.7 acme");
    await Assert
      .That(await SendRawAsync(gateway, AlphaKey, "X-Reports-Tenant:"))
      .StartsWith("HTTP/1.1 200 ");
    // Several tenants: an empty header names none of them.
    var missing = await ReadProblemAsync(await SendAsync(gateway, BetaKey, ""));
    await Assert.That(missing.Status).IsEqualTo(400);
    await Assert.That(missing.Kind).IsEqualTo("InvalidRequest");
    await Assert.That(missing.Detail).Contains("X-Reports-Tenant");
    // Two lines, even if one is empty, stay ambiguous.
    await Assert
      .That(await SendRawAsync(gateway, AlphaKey, "X-Reports-Tenant:", "X-Reports-Tenant: acme"))
      .StartsWith("HTTP/1.1 400 ");
  }

  [Test]
  public async Task A_renderer_that_rejects_the_gateways_key_makes_the_renderer_unavailable()
  {
    var acmeKey = RendererCredential.Generate();
    var otherKey = RendererCredential.Generate();
    var converter = PdfConverter("%PDF-1.7 acme");
    await using var acme = await StartRendererAsync(converter, acmeKey);
    LogCollector logs = new();
    await using var gateway = await GatewayHost.StartAsync(
      [.. OneTenant(acme.Client.BaseAddress!.ToString(), apiKey: otherKey.Credential)],
      builder => builder.Logging.AddProvider(logs)
    );

    using var response = await gateway.PostAsync("""{"html":"<p>x</p>"}""");

    // Not 401: the caller's own credentials are fine; the gateway's are not.
    var problem = await ReadProblemAsync(response);
    await Assert.That(problem.Status).IsEqualTo(503);
    await Assert.That(problem.Kind).IsEqualTo("BrowserUnavailable");
    await Assert.That(problem.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(converter.Calls).IsEqualTo(0);
    var rejected = logs.WithEventId(44).Single();
    await Assert.That(rejected.Level).IsEqualTo(LogLevel.Error);
    await Assert.That(rejected["TenantId"]).IsEqualTo("acme");
    await Assert.That(rejected["StatusCode"]).IsEqualTo(401);
    await Assert
      .That(
        logs.Entries.Any(entry =>
          entry.Message.Contains(otherKey.Credential, StringComparison.Ordinal)
        )
      )
      .IsFalse();
  }

  [Test]
  public async Task Nothing_of_the_callers_request_reaches_the_renderer()
  {
    const string html = "<!doctype html><p>Invoice &amp; \"total\" <b>42</b></p>";
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    await using var gateway = await GatewayHost.StartAsync([
      .. Callers(),
      .. Renderer(0, "acme", renderer.BaseUrl, TestKey),
    ]);

    using var request = ConvertRequest(
      JsonSerializer.Serialize(new Dictionary<string, string> { ["html"] = html }),
      AlphaKey
    );
    request.Headers.Add("X-Reports-Tenant", "acme");
    request.Headers.Add("Authorization", "Bearer caller-token");
    request.Headers.Add("Cookie", "session=caller-cookie");
    request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
    request.Headers.Add("tracestate", "caller=state");
    request.Headers.Add("baggage", "secret=caller-baggage");
    request.Headers.Add("X-Forwarded-For", "203.0.113.7");
    request.Headers.Add("X-Caller-Custom", "caller-custom");
    using var response = await gateway.Client.SendAsync(request, TestToken);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    var received = renderer.Requests.Single();
    await Assert.That(received.Headers["X-Reports-Api-Key"]).IsEqualTo(TestKey);
    string[] allowed =
    [
      "Host",
      "X-Reports-Api-Key",
      "Content-Type",
      "Transfer-Encoding",
      "Content-Length",
    ];
    await Assert
      .That(
        received.Headers.Keys.Where(name =>
          !allowed.Contains(name, StringComparer.OrdinalIgnoreCase)
        )
      )
      .IsEmpty();
    await Assert
      .That(
        received.Headers.Values.Any(value =>
          value.Contains("caller", StringComparison.Ordinal)
          || value.Contains(AlphaKey, StringComparison.Ordinal)
        )
      )
      .IsFalse();
    using var body = JsonDocument.Parse(received.Body);
    await Assert.That(body.RootElement.GetProperty("html").GetString()).IsEqualTo(html);
    await Assert
      .That(body.RootElement.EnumerateObject().Select(property => property.Name))
      .IsEquivalentTo(["html", "options"]);
  }

  [Test]
  public async Task The_validated_options_reach_the_renderer_unchanged()
  {
    var key = RendererCredential.Generate();
    PdfOptions? received = null;
    FakeConverter converter = new(
      async (destination, cancellationToken) =>
      {
        await destination.WriteAsync("%PDF-1.7"u8.ToArray(), cancellationToken);
        return null;
      },
      options => received = options
    );
    await using var renderer = await StartRendererAsync(converter, key);
    await using var gateway = await GatewayHost.StartAsync(
      OneTenant(renderer.Client.BaseAddress!.ToString(), apiKey: key.Credential)
    );

    using var response = await gateway.PostAsync(
      """
      {"html":"<p>x</p>","options":{"orientation":"LANDSCAPE","paperWidth":5.5,"paperHeight":8.5,
      "margins":{"top":1,"left":0.25},"printBackground":false,"scale":1.5,"displayHeaderFooter":true,
      "headerTemplate":"<span class=title></span>","footerTemplate":"<span class=pageNumber></span>",
      "pageRanges":"1-2","preferCSSPageSize":true,"generateTaggedPdf":false,"waitForSignal":"ready",
      "waitTimeoutSeconds":-0.001}}
      """
    );

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(received).IsNotNull();
    await Assert.That(received!.Orientation).IsEqualTo(PageOrientation.Landscape);
    await Assert.That(received.PaperSize.Width).IsEqualTo(5.5);
    await Assert.That(received.PaperSize.Height).IsEqualTo(8.5);
    await Assert.That(received.Margins.Top).IsEqualTo(1);
    await Assert.That(received.Margins.Left).IsEqualTo(0.25);
    await Assert.That(received.Margins.Bottom).IsEqualTo(0.4);
    await Assert.That(received.PrintBackground).IsFalse();
    await Assert.That(received.Scale).IsEqualTo(1.5);
    await Assert.That(received.DisplayHeaderFooter).IsTrue();
    await Assert.That(received.HeaderTemplate).IsEqualTo("<span class=title></span>");
    await Assert.That(received.FooterTemplate).IsEqualTo("<span class=pageNumber></span>");
    await Assert.That(received.PageRanges).IsEqualTo("1-2");
    await Assert.That(received.PreferCssPageSize).IsTrue();
    await Assert.That(received.GenerateTaggedPdf).IsFalse();
    await Assert.That(received.WaitForSignal).IsEqualTo("ready");
    await Assert.That(received.WaitTimeout).IsEqualTo(Timeout.InfiniteTimeSpan);
  }

  [Test]
  public async Task Invalid_requests_are_rejected_without_reaching_the_renderer()
  {
    await using var under = await GatewayUnderTest.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );

    using var blank = await under.PostAsync("""{"html":"  "}""");
    using var badOption = await under.PostAsync(
      """{"html":"<p>x</p>","options":{"orientation":"sideways"}}"""
    );
    using var notJson = await under.PostAsync("not json");

    await Assert.That(blank.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert.That(badOption.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert.That(notJson.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert.That(under.Renderer.Requests).IsEmpty();
  }

  [Test]
  public async Task Gateway_mode_runs_no_engine_and_no_browser()
  {
    await using var under = await GatewayUnderTest.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    var services = under.Gateway.Services;

    await Assert.That(services.GetService<BrowserManager>()).IsNull();
    await Assert
      .That(
        services
          .GetServices<IHostedService>()
          .Any(service => service.GetType().Assembly == typeof(IHtmlToPdfConverter).Assembly)
      )
      .IsFalse();
    await Assert
      .That(services.GetRequiredService<IHtmlToPdfConverter>().GetType().Assembly)
      .IsEqualTo(typeof(ReportsServerApplication).Assembly);

    using var details = await under.Gateway.Client.GetAsync("/health/details", TestToken);
    using var health = JsonDocument.Parse(await details.Content.ReadAsStringAsync(TestToken));
    await Assert.That(details.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(health.RootElement.GetProperty("checks").EnumerateObject().Select(check => check.Name))
      .IsEquivalentTo(["renderer_records"]);
  }

  [Test]
  public async Task The_request_span_names_the_tenant()
  {
    ConcurrentQueue<Activity> stopped = new();
    using ActivityListener listener = new()
    {
      ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
      ActivityStopped = stopped.Enqueue,
    };
    ActivitySource.AddActivityListener(listener);
    await using var renderer = await FakeRenderer.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );
    await using var gateway = await GatewayHost.StartAsync(
      OneTenant(renderer.BaseUrl, tenantId: "span-tenant")
    );

    using var response = await gateway.PostAsync("""{"html":"<p>x</p>"}""");

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    var tagged = await TestEngine.EventuallyAsync(
      () =>
        Task.FromResult(
          stopped.Any(activity =>
            activity.GetTagItem("atli.reports.tenant") as string == "span-tenant"
          )
        ),
      TestEngine.GenerousTimeout
    );
    await Assert.That(tagged).IsTrue();
  }

  [Test]
  public async Task The_openapi_document_describes_the_tenant_header()
  {
    await using var under = await GatewayUnderTest.StartAsync(context =>
      FakeRenderer.WritePdfAsync(context, "%PDF-1.7")
    );

    using var response = await under.Gateway.Client.GetAsync("/openapi/v1.json", TestToken);
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestToken));

    var parameter = document
      .RootElement.GetProperty("paths")
      .GetProperty("/convert")
      .GetProperty("post")
      .GetProperty("parameters")
      .EnumerateArray()
      .Single();
    await Assert.That(parameter.GetProperty("name").GetString()).IsEqualTo("X-Reports-Tenant");
    await Assert.That(parameter.GetProperty("in").GetString()).IsEqualTo("header");
  }

  private static async Task<HttpResponseMessage> SendAsync(
    RunningServer gateway,
    string credential,
    params string[] tenants
  )
  {
    using var request = ConvertRequest("""{"html":"<p>x</p>"}""", credential);
    foreach (var tenant in tenants)
    {
      request.Headers.Add("X-Reports-Tenant", tenant);
    }

    return await gateway.Client.SendAsync(request, TestToken);
  }

  /// <summary>
  /// Sends a conversion over a raw socket, with <paramref name="headers"/> as separate header lines,
  /// and returns the response's status line.
  /// </summary>
  private static async Task<string> SendRawAsync(
    RunningServer gateway,
    string credential,
    params string[] headers
  )
  {
    const string body = """{"html":"<p>x</p>"}""";
    var address = gateway.Client.BaseAddress!;
    using TcpClient client = new();
    await client.ConnectAsync(address.Host, address.Port, TestToken);
    var stream = client.GetStream();
    var request =
      $"POST /convert HTTP/1.1\r\nHost: {address.Authority}\r\nX-Reports-Api-Key: {credential}\r\n"
      + string.Concat(headers.Select(header => header + "\r\n"))
      + $"Content-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(request), TestToken);
    using StreamReader reader = new(stream, Encoding.ASCII);
    return await reader.ReadLineAsync(TestToken) ?? "";
  }

  private static async Task AssertPdfAsync(HttpResponseMessage response, string expected)
  {
    using (response)
    {
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
      await Assert
        .That(response.Content.Headers.ContentType?.MediaType)
        .IsEqualTo("application/pdf");
      await Assert
        .That(response.Content.Headers.ContentDisposition?.FileName)
        .IsEqualTo("output.pdf");
      await Assert
        .That(Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync(TestToken)))
        .IsEqualTo(expected);
    }
  }
}
