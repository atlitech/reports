using Atli.Reports.Engine.Health;

namespace Atli.Reports.Engine.Tests.Health;

/// <summary>
/// How the browser check reduces a launch failure before a health endpoint shows it.
/// </summary>
public class BrowserHealthCheckTests
{
  [Test]
  public async Task A_launch_failure_is_reported_on_one_line()
  {
    var reason = BrowserHealthCheck.SanitizeReason(
      "The browser exited with code 1.\nBrowser output: first\r\n\tsecond \u001b[0mthird  "
    );

    await Assert
      .That(reason)
      .IsEqualTo("The browser exited with code 1. Browser output: first second [0mthird");
  }

  [Test]
  public async Task Credentials_devtools_ids_and_secret_parameters_are_masked()
  {
    var reason = BrowserHealthCheck.SanitizeReason(
      "Proxy http://alice:s3cret@proxy.example:8080 refused; ws://127.0.0.1:9222/devtools/browser/0b5e2c1a-41 closed; --api-key=abc123 token=xyz&page=2 Password=hunter2"
    );

    await Assert
      .That(reason)
      .IsEqualTo(
        "Proxy http://***@proxy.example:8080 refused; ws://127.0.0.1:9222/devtools/browser/*** closed; --api-key=*** token=***&page=2 Password=***"
      );
  }

  [Test]
  public async Task A_long_reason_keeps_its_start_and_its_end()
  {
    var reason = BrowserHealthCheck.SanitizeReason(
      "The browser exited with code 1. Browser output: "
        + string.Concat(Enumerable.Repeat("noise ", 500))
        + "the last words"
    );

    await Assert.That(reason.Length).IsLessThanOrEqualTo(BrowserHealthCheck.MaxReasonLength);
    await Assert.That(reason).StartsWith("The browser exited with code 1. Browser output: noise");
    await Assert.That(reason).Contains(" ... ");
    await Assert.That(reason).EndsWith("the last words");
  }
}
