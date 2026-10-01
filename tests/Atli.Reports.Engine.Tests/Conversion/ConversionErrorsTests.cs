using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Conversion;

namespace Atli.Reports.Engine.Tests.Conversion;

public class ConversionErrorsTests
{
  [Test]
  public async Task FromBrowserError_maps_an_exhausted_pool_to_Busy()
  {
    var error = ConversionErrors.FromBrowserError(
      "Failed to create page",
      new PoolExhaustedError("Page", 10),
      ConversionErrorKind.BrowserUnavailable,
      CancellationToken.None
    );

    await Assert.That(error.Kind).IsEqualTo(ConversionErrorKind.Busy);
    await Assert
      .That(error.Message)
      .IsEqualTo("Failed to create page: Page pool exhausted (max: 10)");
  }

  [Test]
  public async Task FromBrowserError_maps_a_timeout_to_Timeout()
  {
    var timeoutError = ConversionErrors.FromBrowserError(
      "Failed to create page",
      new TimeoutError("Target.createTarget", TimeSpan.FromSeconds(30)),
      ConversionErrorKind.BrowserUnavailable,
      CancellationToken.None
    );
    var wrappedTimeout = ConversionErrors.FromBrowserError(
      "Failed to create page",
      new ChromiumError("Failed to create browser page", new TimeoutException()),
      ConversionErrorKind.BrowserUnavailable,
      CancellationToken.None
    );

    await Assert.That(timeoutError.Kind).IsEqualTo(ConversionErrorKind.Timeout);
    await Assert.That(wrappedTimeout.Kind).IsEqualTo(ConversionErrorKind.Timeout);
  }

  [Test]
  public async Task FromBrowserError_uses_the_fallback_for_other_errors()
  {
    InvalidOperationException cause = new("boom");

    var error = ConversionErrors.FromBrowserError(
      "Failed to set HTML content",
      new RenderError("Failed to set HTML content", cause),
      ConversionErrorKind.RenderFailed,
      CancellationToken.None
    );

    await Assert.That(error.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert.That(error.Exception).IsSameReferenceAs(cause);
  }

  [Test]
  public async Task FromBrowserError_reports_Canceled_when_the_caller_canceled()
  {
    using CancellationTokenSource cancellation = new();
    await cancellation.CancelAsync();

    // The browser layer reports a canceled command as a failure; the caller's token decides.
    var error = ConversionErrors.FromBrowserError(
      "Failed to set HTML content",
      new RenderError("Failed to set HTML content", new TimeoutException()),
      ConversionErrorKind.RenderFailed,
      cancellation.Token
    );

    await Assert.That(error.Kind).IsEqualTo(ConversionErrorKind.Canceled);
  }

  [Test]
  public async Task FromException_maps_timeouts_cancellation_and_everything_else()
  {
    using CancellationTokenSource cancellation = new();
    await cancellation.CancelAsync();

    var timeout = ConversionErrors.FromException(
      "PDF generation failed",
      new TimeoutException(),
      ConversionErrorKind.RenderFailed,
      CancellationToken.None
    );
    var canceled = ConversionErrors.FromException(
      "PDF generation failed",
      new OperationCanceledException(cancellation.Token),
      ConversionErrorKind.RenderFailed,
      cancellation.Token
    );
    var other = ConversionErrors.FromException(
      "PDF generation failed",
      new InvalidOperationException(),
      ConversionErrorKind.RenderFailed,
      CancellationToken.None
    );

    await Assert.That(timeout.Kind).IsEqualTo(ConversionErrorKind.Timeout);
    await Assert.That(canceled.Kind).IsEqualTo(ConversionErrorKind.Canceled);
    await Assert.That(other.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert.That(other.Message).IsEqualTo("PDF generation failed");
  }

  [Test]
  public async Task FromLaunchException_maps_a_startup_timeout_to_BrowserUnavailable()
  {
    var error = ConversionErrors.FromLaunchException(
      new TimeoutException("DevToolsActivePort did not appear"),
      CancellationToken.None
    );

    await Assert.That(error.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
  }
}
