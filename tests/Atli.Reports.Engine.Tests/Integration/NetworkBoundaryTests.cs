using Atli.Reports.Engine.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Engine.Tests.Integration;

[NotInParallel("chrome")]
public class NetworkBoundaryTests
{
  [Test]
  [Arguments(ReportsEngineNetworkMode.Disabled)]
  [Arguments(ReportsEngineNetworkMode.AllowList)]
  public async Task Subresources_scripts_workers_and_websockets_cannot_reach_loopback(
    ReportsEngineNetworkMode mode
  )
  {
    await using TestHttpServer server = new();
    await using var engine = TestEngine.Create(options =>
    {
      options.Network.Mode = mode;
      options.Network.AllowedOrigins.Add(server.BaseUrl);
    });
    var converter = engine.GetRequiredService<IHtmlToPdfConverter>();
    var websocket = server.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal);
    var html = $$"""
      <!doctype html><html><head><title>restricted</title>
      <link rel="stylesheet" href="{{server.BaseUrl}}/style">
      <style>@font-face { font-family: remote; src: url('{{server.BaseUrl}}/font'); } body { font-family: remote; background: url('{{server.BaseUrl}}/css-image'); }</style>
      </head><body>Self-contained PDF<img src="{{server.BaseUrl}}/image">
      <iframe src="{{server.BaseUrl}}/frame"></iframe>
      <script src="{{server.BaseUrl}}/script"></script><script>
      fetch('{{server.BaseUrl}}/fetch').catch(() => {});
      new WebSocket('{{websocket}}/socket').onerror = () => {};
      const worker = new Worker(URL.createObjectURL(new Blob([
        "fetch('{{server.BaseUrl}}/worker').catch(() => {}); new WebSocket('{{websocket}}/worker-socket');"
      ], { type: 'text/javascript' })));
      window.open('{{server.BaseUrl}}/popup');
      setTimeout(() => { document.title = 'restricted-ready'; window.pdfReady(); }, 1000);
      </script></body></html>
      """;

    var result = await converter.ConvertAsync(
      html,
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TestEngine.GenerousTimeout }
    );

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.PolicyDenied);
    await Assert.That(server.Requests.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Document_networking_is_disabled_by_default()
  {
    await using TestHttpServer server = new();
    await using var engine = TestEngine.Create();
    var converter = engine.GetRequiredService<IHtmlToPdfConverter>();

    var result = await converter.ConvertAsync(
      $"<script>location.href = '{server.BaseUrl}/navigate';</script>"
    );

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.PolicyDenied);
    await Assert.That(server.Requests.Count).IsEqualTo(0);
  }

  [Test]
  [Arguments("worker")]
  [Arguments("websocket")]
  [Arguments("popup")]
  public async Task Targets_outside_page_fetch_interception_are_also_denied(string target)
  {
    await using TestHttpServer server = new();
    await using var engine = TestEngine.Create(options =>
    {
      options.Network.Mode = ReportsEngineNetworkMode.Disabled;
      options.Browser.ExtraArguments.Add("--disable-popup-blocking");
    });
    var converter = engine.GetRequiredService<IHtmlToPdfConverter>();
    var script = target switch
    {
      "worker" =>
        $$"""new Worker(URL.createObjectURL(new Blob(["fetch('{{server.BaseUrl}}/worker').catch(() => {});"], { type: 'text/javascript' })));""",
      "websocket" =>
        $"new WebSocket('{server.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal)}/socket');",
      _ => $"window.open('{server.BaseUrl}/popup');",
    };
    var result = await converter.ConvertAsync(
      $"<script>{script} setTimeout(() => window.pdfReady(), 1500);</script>",
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TestEngine.GenerousTimeout }
    );

    // Check the actual destination first: an unexpected success must not hide a network bypass.
    await Assert.That(server.Requests.Count).IsEqualTo(0);
    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.PolicyDenied);
  }

  [Test]
  public async Task Inline_scripts_and_data_URI_assets_remain_available_without_network()
  {
    await using var engine = TestEngine.Create(options =>
      options.Network.Mode = ReportsEngineNetworkMode.Disabled
    );
    var converter = engine.GetRequiredService<IHtmlToPdfConverter>();

    var pdf = await converter.ConvertToBytesAsync(
      """
      <html><head><title>pending</title></head><body>
      <img src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='10' height='10'%3E%3Crect width='10' height='10' fill='red'/%3E%3C/svg%3E">
      <script>document.title = 'inline-ready';</script></body></html>
      """
    );

    await Assert.That(PdfInspector.ReadTitle(pdf)).IsEqualTo("inline-ready");
  }

  [Test]
  [Arguments("main")]
  [Arguments("frame")]
  [Arguments("popup")]
  public async Task Local_file_documents_cannot_be_read_through_navigation(string target)
  {
    var path = Path.Combine(Path.GetTempPath(), $"atli-file-canary-{Guid.NewGuid():N}.html");
    await File.WriteAllTextAsync(
      path,
      """
      <html><head><title>local-file-visible</title></head><body>local-file-canary<script>
      window.parent.postMessage('local-file-visible', '*');
      if (window.opener) window.opener.postMessage('local-file-visible', '*');
      </script></body></html>
      """
    );
    try
    {
      await using var engine = TestEngine.Create(options =>
      {
        options.Network.Mode = ReportsEngineNetworkMode.Disabled;
        options.Browser.ExtraArguments.Add("--disable-popup-blocking");
      });
      var converter = engine.GetRequiredService<IHtmlToPdfConverter>();
      var uri = new Uri(path).AbsoluteUri;
      var script = target switch
      {
        "main" => $"location.href = '{uri}';",
        "frame" =>
          $"const frame = document.createElement('iframe'); frame.src = '{uri}'; document.body.appendChild(frame);",
        _ => $"window.open('{uri}');",
      };
      var result = await converter.ConvertAsync(
        $$"""
        <html><head><title>original-document</title></head><body><script>
        window.addEventListener('message', event => { if (event.data === 'local-file-visible') document.title = event.data; });
        {{script}}
        setTimeout(() => window.pdfReady(), 1000);
        </script></body></html>
        """,
        new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TestEngine.GenerousTimeout }
      );

      if (result.TryPickT1(out var error, out var pdf))
      {
        await Assert.That(error.Kind).IsEqualTo(ConversionErrorKind.PolicyDenied);
      }
      else
      {
        // Chromium may reject file access before a request reaches Fetch interception.
        await using (pdf)
        {
          using MemoryStream bytes = new();
          await pdf.CopyToAsync(bytes);
          await Assert.That(PdfInspector.ReadTitle(bytes.ToArray())).IsEqualTo("original-document");
        }
      }
    }
    finally
    {
      File.Delete(path);
    }
  }
}
