using Atli.Reports.Engine.Chromium.Browser;

namespace Atli.Reports.Engine.Tests.Chromium;

public class BrowserProcessTests
{
  [Test]
  public async Task The_DevTools_endpoint_is_read_from_the_browser_output()
  {
    var found = BrowserProcess.TryParseEndpoint(
      "DevTools listening on ws://127.0.0.1:41234/devtools/browser/0b1c2d3e",
      out var endpoint
    );

    await Assert.That(found).IsTrue();
    await Assert
      .That(endpoint)
      .IsEqualTo(new Uri("ws://127.0.0.1:41234/devtools/browser/0b1c2d3e"));
  }

  [Test]
  [Arguments("")]
  [Arguments("[1001/120000.000:ERROR:gpu_init.cc(1)] Passthrough is not supported")]
  [Arguments("DevTools listening on http://127.0.0.1:1/")]
  public async Task Other_output_is_not_an_endpoint(string line)
  {
    await Assert.That(BrowserProcess.TryParseEndpoint(line, out _)).IsFalse();
  }
}
