using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Protocol;
using Atli.Reports.Engine.Conversion;

namespace Atli.Reports.Engine.Tests.Conversion;

public class ConversionErrorsTests
{
  [Test]
  public async Task A_busy_engine_is_Busy()
  {
    var error = ConversionErrors.FromException(
      "Failed to open a browser page",
      new ConversionBusyException("The engine is busy."),
      CancellationToken.None
    );

    await Assert.That(error.Kind).IsEqualTo(ConversionErrorKind.Busy);
    await Assert.That(error.Message).IsEqualTo("The engine is busy.");
  }

  [Test]
  public async Task A_missing_or_dead_browser_is_BrowserUnavailable()
  {
    var missing = ConversionErrors.FromException(
      "Failed to open a browser page",
      new BrowserUnavailableException("No Chrome executable was found."),
      CancellationToken.None
    );
    var disconnected = ConversionErrors.FromException(
      "PDF generation failed",
      new BrowserConnectionClosedException("The DevTools connection to the browser was lost."),
      CancellationToken.None
    );

    await Assert.That(missing.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert
      .That(missing.Message)
      .IsEqualTo("Failed to open a browser page: No Chrome executable was found.");
    await Assert.That(disconnected.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
  }

  [Test]
  public async Task Timeouts_are_Timeout()
  {
    var error = ConversionErrors.FromException(
      "PDF generation failed",
      new DevToolsTimeoutException("Page.printToPDF", TimeSpan.FromSeconds(30)),
      CancellationToken.None
    );

    await Assert.That(error.Kind).IsEqualTo(ConversionErrorKind.Timeout);
  }

  [Test]
  public async Task Protocol_errors_and_crashed_pages_are_RenderFailed()
  {
    var rejected = ConversionErrors.FromException(
      "PDF generation failed",
      new DevToolsProtocolException("Page.printToPDF", -32000, "Page range syntax error"),
      CancellationToken.None
    );
    var crashed = ConversionErrors.FromException(
      "Failed waiting for the page",
      new TargetCrashedException(),
      CancellationToken.None
    );

    await Assert.That(rejected.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert
      .That(rejected.Message)
      .IsEqualTo("PDF generation failed: Page.printToPDF failed: Page range syntax error (-32000)");
    await Assert.That(crashed.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
  }

  [Test]
  public async Task Anything_else_is_RenderFailed_with_the_stage_as_message()
  {
    InvalidOperationException cause = new("boom");

    var error = ConversionErrors.FromException(
      "Failed to set HTML content",
      cause,
      CancellationToken.None
    );

    await Assert.That(error.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert.That(error.Message).IsEqualTo("Failed to set HTML content");
    await Assert.That(error.Exception).IsSameReferenceAs(cause);
  }

  [Test]
  public async Task Cancellation_by_the_caller_wins()
  {
    using CancellationTokenSource cancellation = new();
    await cancellation.CancelAsync();

    // The browser layer reports a canceled wait as whatever failed; the caller's token decides.
    var error = ConversionErrors.FromException(
      "PDF generation failed",
      new BrowserConnectionClosedException(),
      cancellation.Token
    );

    await Assert.That(error.Kind).IsEqualTo(ConversionErrorKind.Canceled);
  }
}
