using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Tests.Integration;

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

  [Test]
  [Arguments(SandboxFailureTests.NoUsableSandbox)]
  [Arguments("Check failed: sys_chroot(\"/proc/self/fdinfo/\") == 0")]
  public async Task Chromium_failing_to_create_its_sandbox_is_recognized(string line)
  {
    await Assert.That(BrowserProcess.IsSandboxFailure(line)).IsTrue();
  }

  [Test]
  [Arguments("")]
  [Arguments(
    "[1002/231115.259212:FATAL:content/browser/zygote_host/zygote_host_impl_linux.cc:237] Zygote process exited prematurely with exit code -1"
  )]
  [Arguments(
    "/opt/chrome-headless-shell/chrome-headless-shell: error while loading shared libraries: libnss3.so: cannot open shared object file: No such file or directory"
  )]
  [Arguments(
    "[1002/120000.000:FATAL:content/browser/zygote_host/zygote_host_impl_linux.cc:120] Running as root without --no-sandbox is not supported. See https://crbug.com/638180."
  )]
  public async Task Other_output_is_not_a_sandbox_failure(string line)
  {
    await Assert.That(BrowserProcess.IsSandboxFailure(line)).IsFalse();
  }
}
