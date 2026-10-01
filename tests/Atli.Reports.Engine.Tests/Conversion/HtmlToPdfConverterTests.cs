using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Chromium.Protocol;
using Atli.Reports.Engine.Conversion;
using Atli.Reports.Engine.Health;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atli.Reports.Engine.Tests.Conversion;

/// <summary>
/// Exercises the converter's flow, error mapping, and health recording against a fake browser, so
/// no real browser is launched.
/// </summary>
public class HtmlToPdfConverterTests
{
  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task Blank_html_is_an_invalid_request_and_opens_no_page(string html)
  {
    FakeBrowserProvider browsers = new(_ => throw new InvalidOperationException("unreachable"));
    var (converter, tracker) = CreateConverter(browsers);

    var result = await converter.ConvertAsync(html);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.InvalidRequest);
    await Assert.That(browsers.Calls).IsEqualTo(0);
    await Assert.That(tracker.GetHealthStatus().Total).IsEqualTo(0);
  }

  [Test]
  public async Task Blank_signal_name_is_an_invalid_request()
  {
    FakeBrowserProvider browsers = new(_ => throw new InvalidOperationException("unreachable"));
    var (converter, _) = CreateConverter(browsers);

    var result = await converter.ConvertAsync("<p>x</p>", new PdfOptions { WaitForSignal = " " });

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.InvalidRequest);
    await Assert.That(browsers.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task Negative_signal_timeout_is_an_invalid_request()
  {
    FakeBrowserProvider browsers = new(_ => throw new InvalidOperationException("unreachable"));
    var (converter, _) = CreateConverter(browsers);

    var result = await converter.ConvertAsync(
      "<p>x</p>",
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TimeSpan.FromSeconds(-1) }
    );

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.InvalidRequest);
    await Assert.That(browsers.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task Without_a_signal_the_page_loads_before_it_prints()
  {
    FakePage page = new();
    var (converter, tracker) = CreateConverter(new FakeBrowserProvider(_ => page));

    var result = await converter.ConvertAsync("<p>x</p>");

    await Assert.That(result.IsT0).IsTrue();
    await using var pdf = result.AsT0;
    await Assert.That(pdf.Position).IsEqualTo(0);
    await Assert.That(pdf.Length).IsEqualTo(FakePage.Pdf.Length);
    await Assert.That(page.Calls).IsEquivalentTo(["SetContent", "WaitForLoad", "Print"]);
    await Assert.That(page.Disposed).IsTrue();
    await Assert.That(tracker.GetHealthStatus().Successes).IsEqualTo(1);
  }

  [Test]
  public async Task With_a_signal_the_signal_is_enabled_before_the_content_is_set()
  {
    FakePage page = new();
    var (converter, _) = CreateConverter(new FakeBrowserProvider(_ => page));

    var result = await converter.ConvertAsync(
      "<p>x</p>",
      new PdfOptions { WaitForSignal = "pdfReady" }
    );

    await Assert.That(result.IsT0).IsTrue();
    await Assert
      .That(page.Calls)
      .IsEquivalentTo(["EnableSignal:pdfReady", "SetContent", "WaitForSignal", "Print"]);
  }

  [Test]
  public async Task A_signal_that_never_comes_is_SignalTimeout()
  {
    FakePage page = new() { SignalArrives = false };
    var (converter, tracker) = CreateConverter(new FakeBrowserProvider(_ => page));

    var result = await converter.ConvertAsync(
      "<p>x</p>",
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TimeSpan.FromSeconds(2) }
    );

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.SignalTimeout);
    await Assert.That(page.Disposed).IsTrue();
    await Assert.That(tracker.GetHealthStatus().Failures).IsEqualTo(1);
  }

  [Test]
  public async Task An_unavailable_browser_is_BrowserUnavailable_and_counts_as_a_failure()
  {
    FakeBrowserProvider browsers = new(_ =>
      throw new BrowserUnavailableException("No Chrome executable was found.")
    );
    var (converter, tracker) = CreateConverter(browsers);

    var result = await converter.ConvertAsync("<p>x</p>");

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert
      .That(result.AsT1.Message)
      .IsEqualTo("Failed to open a browser page: No Chrome executable was found.");
    await Assert.That(tracker.GetHealthStatus().Failures).IsEqualTo(1);
  }

  [Test]
  public async Task A_command_timeout_is_Timeout_and_the_page_is_disposed()
  {
    FakePage page = new()
    {
      PrintFailure = new DevToolsTimeoutException("Page.printToPDF", TimeSpan.FromSeconds(30)),
    };
    var (converter, _) = CreateConverter(new FakeBrowserProvider(_ => page));

    var result = await converter.ConvertAsync("<p>x</p>");

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Timeout);
    await Assert.That(page.Disposed).IsTrue();
  }

  [Test]
  public async Task Options_the_browser_rejects_are_RenderFailed()
  {
    FakePage page = new()
    {
      PrintFailure = new DevToolsProtocolException(
        "Page.printToPDF",
        -32000,
        "Page range syntax error"
      ),
    };
    var (converter, _) = CreateConverter(new FakeBrowserProvider(_ => page));

    var result = await converter.ConvertAsync("<p>x</p>");

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert
      .That(result.AsT1.Message)
      .StartsWith("PDF generation failed: Page.printToPDF failed:");
  }

  [Test]
  public async Task Cancellation_is_Canceled_and_does_not_count_as_a_failure()
  {
    using CancellationTokenSource cancellation = new();
    FakeBrowserProvider browsers = new(token =>
    {
      cancellation.Cancel();
      token.ThrowIfCancellationRequested();
      throw new InvalidOperationException("unreachable");
    });
    var (converter, tracker) = CreateConverter(browsers);

    var result = await converter.ConvertAsync("<p>x</p>", cancellationToken: cancellation.Token);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Canceled);
    await Assert.That(tracker.GetHealthStatus().Total).IsEqualTo(0);
  }

  [Test]
  public async Task An_already_canceled_token_opens_no_page()
  {
    using CancellationTokenSource cancellation = new();
    await cancellation.CancelAsync();
    FakeBrowserProvider browsers = new(_ => throw new InvalidOperationException("unreachable"));
    var (converter, _) = CreateConverter(browsers);

    var result = await converter.ConvertAsync("<p>x</p>", cancellationToken: cancellation.Token);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Canceled);
    await Assert.That(browsers.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task A_full_queue_is_Busy_and_does_not_count_as_a_failure()
  {
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    FakePage blocked = new() { PrintGate = release.Task };
    var (converter, tracker) = CreateConverter(
      new FakeBrowserProvider(_ => blocked),
      new ReportsEngineConcurrencyOptions { MaxConcurrentConversions = 1, MaxQueueLength = 0 }
    );

    var running = converter.ConvertAsync("<p>first</p>").AsTask();
    await blocked.Printing.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var rejected = await converter.ConvertAsync("<p>second</p>");
    release.SetResult();
    var first = await running;

    await Assert.That(rejected.AsT1.Kind).IsEqualTo(ConversionErrorKind.Busy);
    await Assert.That(first.IsT0).IsTrue();
    await Assert.That(tracker.GetHealthStatus().Failures).IsEqualTo(0);
  }

  [Test]
  public async Task The_streaming_overload_writes_the_pdf_to_the_destination()
  {
    var (converter, _) = CreateConverter(new FakeBrowserProvider(_ => new FakePage()));
    using MemoryStream destination = new();

    var result = await converter.ConvertAsync("<p>x</p>", destination);

    await Assert.That(result.IsT0).IsTrue();
    await Assert.That(destination.ToArray()).IsEquivalentTo(FakePage.Pdf);
  }

  [Test]
  public async Task The_streaming_overload_returns_the_error_and_writes_nothing()
  {
    FakeBrowserProvider browsers = new(_ => throw new BrowserUnavailableException("missing"));
    var (converter, _) = CreateConverter(browsers);
    using MemoryStream destination = new();

    var result = await converter.ConvertAsync("<p>x</p>", destination);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert.That(destination.Length).IsEqualTo(0);
  }

  [Test]
  public async Task Exceptions_from_the_destination_propagate_unchanged()
  {
    FakePage page = new();
    var (converter, _) = CreateConverter(new FakeBrowserProvider(_ => page));
    IOException failure = new("The client disconnected.");

    var thrown = await Assert
      .That(async () => await converter.ConvertAsync("<p>x</p>", new ThrowingStream(failure)))
      .Throws<IOException>();

    await Assert.That(thrown).IsSameReferenceAs(failure);
    await Assert.That(page.Disposed).IsTrue();
  }

  [Test]
  public async Task The_streaming_overload_rejects_a_read_only_destination()
  {
    FakeBrowserProvider browsers = new(_ => throw new InvalidOperationException("unreachable"));
    var (converter, _) = CreateConverter(browsers);
    using MemoryStream readOnly = new([], writable: false);

    await Assert
      .That(async () => await converter.ConvertAsync("<p>x</p>", readOnly))
      .Throws<ArgumentException>();
  }

  private static (HtmlToPdfConverter Converter, ConversionHealthTracker Tracker) CreateConverter(
    IBrowserProvider browsers,
    ReportsEngineConcurrencyOptions? concurrency = null
  )
  {
    ConversionHealthTracker tracker = new(TimeProvider.System);
    HtmlToPdfConverter converter = new(
      browsers,
      new ConversionLimiter(concurrency ?? new ReportsEngineConcurrencyOptions()),
      tracker,
      NullLogger<HtmlToPdfConverter>.Instance
    );
    return (converter, tracker);
  }

  private sealed class FakeBrowserProvider(Func<CancellationToken, IConversionPage> open)
    : IBrowserProvider
  {
    public int Calls { get; private set; }

    public ValueTask<IConversionPage> OpenPageAsync(CancellationToken cancellationToken)
    {
      Calls++;
      return ValueTask.FromResult(open(cancellationToken));
    }
  }

  private sealed class FakePage : IConversionPage
  {
    public static readonly byte[] Pdf = "%PDF-1.4 fake %%EOF"u8.ToArray();

    public List<string> Calls { get; } = [];

    public bool SignalArrives { get; init; } = true;

    public Exception? PrintFailure { get; init; }

    public Task PrintGate { get; init; } = Task.CompletedTask;

    public TaskCompletionSource Printing { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Disposed { get; private set; }

    public Task EnableSignalAsync(string signalName, CancellationToken cancellationToken)
    {
      Calls.Add($"EnableSignal:{signalName}");
      return Task.CompletedTask;
    }

    public Task SetContentAsync(string html, CancellationToken cancellationToken)
    {
      Calls.Add("SetContent");
      return Task.CompletedTask;
    }

    public Task<bool> WaitForSignalAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
      Calls.Add("WaitForSignal");
      return Task.FromResult(SignalArrives);
    }

    public Task WaitForLoadAsync(CancellationToken cancellationToken)
    {
      Calls.Add("WaitForLoad");
      return Task.CompletedTask;
    }

    public async Task PrintToPdfAsync(
      PdfOptions options,
      Stream destination,
      CancellationToken cancellationToken
    )
    {
      Calls.Add("Print");
      Printing.TrySetResult();
      await PrintGate.WaitAsync(cancellationToken);
      if (PrintFailure is not null)
      {
        throw PrintFailure;
      }

      try
      {
        await destination.WriteAsync(Pdf, cancellationToken);
      }
      catch (Exception exception) when (exception is not OperationCanceledException)
      {
        throw new DestinationWriteException(exception);
      }
    }

    public ValueTask DisposeAsync()
    {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class ThrowingStream(Exception failure) : Stream
  {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
      throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw failure;

    public override ValueTask WriteAsync(
      ReadOnlyMemory<byte> buffer,
      CancellationToken cancellationToken = default
    ) => throw failure;
  }
}
