using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Conversion;
using Atli.Reports.Engine.Health;
using Atli.Reports.Engine.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using OneOf;

namespace Atli.Reports.Engine.Tests.Conversion;

/// <summary>
/// Exercises the converter's error mapping and health recording against a fake browser,
/// so no real browser is launched.
/// </summary>
public class HtmlToPdfConverterTests
{
  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task Blank_html_is_an_invalid_request_and_launches_no_browser(string html)
  {
    FakeBrowserFactory factory = new(_ => throw new InvalidOperationException("unreachable"));
    var (converter, tracker) = CreateConverter(factory);

    var result = await converter.ConvertAsync(html);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.InvalidRequest);
    await Assert.That(factory.Calls).IsEqualTo(0);
    await Assert.That(tracker.GetHealthStatus().Total).IsEqualTo(0);
  }

  [Test]
  public async Task Blank_signal_name_is_an_invalid_request()
  {
    FakeBrowserFactory factory = new(_ => throw new InvalidOperationException("unreachable"));
    var (converter, _) = CreateConverter(factory);

    var result = await converter.ConvertAsync("<p>x</p>", new PdfOptions { WaitForSignal = " " });

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.InvalidRequest);
    await Assert.That(factory.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task Negative_signal_timeout_is_an_invalid_request()
  {
    FakeBrowserFactory factory = new(_ => throw new InvalidOperationException("unreachable"));
    var (converter, _) = CreateConverter(factory);

    var result = await converter.ConvertAsync(
      "<p>x</p>",
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TimeSpan.FromSeconds(-1) }
    );

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.InvalidRequest);
    await Assert.That(factory.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task A_missing_browser_is_BrowserUnavailable_and_counts_as_a_failure()
  {
    FakeBrowserFactory factory = new(_ =>
      ValueTask.FromResult<OneOf<IBrowser, BrowserError>>(
        new ChromiumError("Could not find browser at ''")
      )
    );
    var (converter, tracker) = CreateConverter(factory);

    var result = await converter.ConvertAsync("<p>x</p>");

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert
      .That(result.AsT1.Message)
      .IsEqualTo("Failed to create browser: Could not find browser at ''");
    await Assert.That(tracker.GetHealthStatus().Failures).IsEqualTo(1);
  }

  [Test]
  public async Task A_browser_that_throws_while_starting_is_BrowserUnavailable()
  {
    FakeBrowserFactory factory = new(_ => throw new TimeoutException("no DevToolsActivePort"));
    var (converter, _) = CreateConverter(factory);

    var result = await converter.ConvertAsync("<p>x</p>");

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert.That(result.AsT1.Exception).IsTypeOf<TimeoutException>();
  }

  [Test]
  public async Task An_exhausted_page_pool_is_Busy_and_the_browser_is_disposed()
  {
    FakeBrowser browser = new(new PoolExhaustedError("Page", 10));
    FakeBrowserFactory factory = new(_ =>
      ValueTask.FromResult<OneOf<IBrowser, BrowserError>>(browser)
    );
    var (converter, _) = CreateConverter(factory);

    var result = await converter.ConvertAsync("<p>x</p>");

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Busy);
    await Assert.That(browser.Disposed).IsTrue();
  }

  [Test]
  public async Task A_page_creation_timeout_is_Timeout()
  {
    FakeBrowser browser = new(
      new ChromiumError("Failed to create browser page", new TimeoutException())
    );
    FakeBrowserFactory factory = new(_ =>
      ValueTask.FromResult<OneOf<IBrowser, BrowserError>>(browser)
    );
    var (converter, _) = CreateConverter(factory);

    var result = await converter.ConvertAsync("<p>x</p>");

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Timeout);
  }

  [Test]
  public async Task Cancellation_is_Canceled_and_does_not_count_as_a_failure()
  {
    using CancellationTokenSource cancellation = new();
    FakeBrowserFactory factory = new(async token =>
    {
      await cancellation.CancelAsync();
      token.ThrowIfCancellationRequested();
      throw new InvalidOperationException("unreachable");
    });
    var (converter, tracker) = CreateConverter(factory);

    var result = await converter.ConvertAsync("<p>x</p>", cancellationToken: cancellation.Token);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Canceled);
    await Assert.That(tracker.GetHealthStatus().Total).IsEqualTo(0);
  }

  [Test]
  public async Task An_already_canceled_token_launches_no_browser()
  {
    using CancellationTokenSource cancellation = new();
    await cancellation.CancelAsync();
    FakeBrowserFactory factory = new(_ => throw new InvalidOperationException("unreachable"));
    var (converter, _) = CreateConverter(factory);

    var result = await converter.ConvertAsync("<p>x</p>", cancellationToken: cancellation.Token);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Canceled);
    await Assert.That(factory.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task The_streaming_overload_returns_the_error_and_writes_nothing()
  {
    FakeBrowserFactory factory = new(_ =>
      ValueTask.FromResult<OneOf<IBrowser, BrowserError>>(new ChromiumError("missing"))
    );
    var (converter, _) = CreateConverter(factory);
    using MemoryStream destination = new();

    var result = await converter.ConvertAsync("<p>x</p>", destination);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert.That(destination.Length).IsEqualTo(0);
  }

  [Test]
  public async Task The_streaming_overload_rejects_a_read_only_destination()
  {
    FakeBrowserFactory factory = new(_ => throw new InvalidOperationException("unreachable"));
    var (converter, _) = CreateConverter(factory);
    using MemoryStream readOnly = new([], writable: false);

    await Assert
      .That(async () => await converter.ConvertAsync("<p>x</p>", readOnly))
      .Throws<ArgumentException>();
  }

  private static (HtmlToPdfConverter Converter, ConversionHealthTracker Tracker) CreateConverter(
    IBrowserFactory factory
  )
  {
    ConversionHealthTracker tracker = new(TimeProvider.System);
    HtmlToPdfConverter converter = new(
      factory,
      new ChromiumPdfGenerator(NullLogger<ChromiumPdfGenerator>.Instance),
      tracker,
      NullLogger<HtmlToPdfConverter>.Instance
    );
    return (converter, tracker);
  }

  private sealed class FakeBrowserFactory(
    Func<CancellationToken, ValueTask<OneOf<IBrowser, BrowserError>>> create
  ) : IBrowserFactory
  {
    public int Calls { get; private set; }

    public ValueTask<OneOf<IBrowser, BrowserError>> CreateBrowserAsync(
      CancellationToken ct = default
    )
    {
      Calls++;
      return create(ct);
    }
  }

  private sealed class FakeBrowser(BrowserError pageError) : IBrowser
  {
    public bool Disposed { get; private set; }

    public ValueTask<OneOf<ChromiumPage, BrowserError>> CreatePageAsync(
      CancellationToken ct = default
    ) => ValueTask.FromResult<OneOf<ChromiumPage, BrowserError>>(pageError);

    public void ReleasePage(ChromiumPage page) =>
      throw new InvalidOperationException("No page was created.");

    public ValueTask DisposePageAsync(ChromiumPage page) =>
      throw new InvalidOperationException("No page was created.");

    public ValueTask DisposeAsync()
    {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }
}
