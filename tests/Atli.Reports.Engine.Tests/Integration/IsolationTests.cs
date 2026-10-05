using Atli.Reports.Engine.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Engine.Tests.Integration;

/// <summary>
/// Conversions for different tenants share one browser, so nothing one document stores may be
/// visible to the next. The documents navigate to a real origin (a local HTTP server) because the
/// engine's own blank document has an opaque origin that cannot store anything.
/// </summary>
[NotInParallel("chrome")]
public class IsolationTests
{
  private const string Report = """
    document.title = 'cookie=' + document.cookie
      + ';local=' + localStorage.getItem('tenant')
      + ';session=' + sessionStorage.getItem('tenant');
    """;

  private static readonly PdfOptions WaitForSignal = new()
  {
    WaitForSignal = "pdfReady",
    WaitTimeout = TestEngine.GenerousTimeout,
  };

  [Test]
  public async Task Cookies_and_storage_written_in_one_conversion_are_invisible_to_the_next()
  {
    await using var provider = TestEngine.Create(options =>
      options.Network.Mode = ReportsEngineNetworkMode.Unrestricted
    );
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    await using TestHttpServer server = new();
    server
      .Map(
        "/write",
        $$"""
        <!DOCTYPE html><html><head><title>pending</title></head><body><script>
          document.cookie = 'tenant=first; path=/; max-age=3600';
          localStorage.setItem('tenant', 'first');
          sessionStorage.setItem('tenant', 'first');
          {{Report}}
          window.pdfReady();
        </script></body></html>
        """
      )
      .Map(
        "/read",
        $$"""
        <!DOCTYPE html><html><head><title>pending</title></head><body><script>
          {{Report}}
          window.pdfReady();
        </script></body></html>
        """
      );

    var written = await converter.ConvertToBytesAsync(Navigate(server, "/write"), WaitForSignal);
    var read = await converter.ConvertToBytesAsync(Navigate(server, "/read"), WaitForSignal);

    // The first document could store and read back its data, so the second one's view is meaningful.
    await Assert
      .That(PdfInspector.ReadTitle(written))
      .IsEqualTo("cookie=tenant=first;local=first;session=first");
    await Assert.That(PdfInspector.ReadTitle(read)).IsEqualTo("cookie=;local=null;session=null");
  }

  [Test]
  public async Task Cached_responses_are_not_shared_between_conversions()
  {
    await using var provider = TestEngine.Create(options =>
      options.Network.Mode = ReportsEngineNetworkMode.Unrestricted
    );
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    await using TestHttpServer server = new();
    server
      .Map("/style.css", "body { color: black; }", "text/css", cacheControl: "public, max-age=3600")
      .Map(
        "/page",
        """
        <!DOCTYPE html><html><head><link rel="stylesheet" href="/style.css"></head><body>
        <script>window.addEventListener('load', function () { window.pdfReady(); });</script>
        </body></html>
        """
      );

    await converter.ConvertToBytesAsync(Navigate(server, "/page"), WaitForSignal);
    await converter.ConvertToBytesAsync(Navigate(server, "/page"), WaitForSignal);

    await Assert.That(server.Requests["/style.css"].Count).IsEqualTo(2);
  }

  private static string Navigate(TestHttpServer server, string path) =>
    $"<script>location.href = '{server.BaseUrl}{path}';</script>";
}
