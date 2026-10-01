using System.Diagnostics;
using System.Diagnostics.Metrics;
using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Chromium.Protocol;
using Atli.Reports.Engine.Conversion;
using Atli.Reports.Engine.Diagnostics;
using Atli.Reports.Engine.Health;
using Atli.Reports.Engine.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Enums;

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
  public async Task Running_out_of_the_conversion_timeout_is_Timeout()
  {
    FakePage page = new() { PrintGate = new TaskCompletionSource().Task };
    var (converter, tracker) = CreateConverter(
      new FakeBrowserProvider(_ => page),
      conversionTimeout: TimeSpan.FromMilliseconds(200)
    );

    var result = await converter.ConvertAsync("<p>x</p>");

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Timeout);
    await Assert.That(result.AsT1.Message).Contains("conversion timeout");
    await Assert.That(page.Disposed).IsTrue();
    await Assert.That(tracker.GetHealthStatus().Failures).IsEqualTo(1);
  }

  [Test]
  public async Task The_conversion_timeout_also_covers_the_wait_for_a_turn()
  {
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    FakePage blocked = new() { PrintGate = release.Task };
    var (converter, _) = CreateConverter(
      new FakeBrowserProvider(_ => blocked),
      new ReportsEngineConcurrencyOptions { MaxConcurrentConversions = 1, MaxQueueLength = 5 },
      conversionTimeout: TimeSpan.FromMilliseconds(300)
    );

    var running = converter.ConvertAsync("<p>first</p>").AsTask();
    await blocked.Printing.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var queued = await converter.ConvertAsync("<p>second</p>");
    release.SetResult();
    await running;

    await Assert.That(queued.AsT1.Kind).IsEqualTo(ConversionErrorKind.Timeout);
  }

  [Test]
  public async Task Conversions_are_measured_by_outcome()
  {
    List<(double Seconds, string? Outcome)> measurements = [];
    using MeterListener listener = new();
    listener.InstrumentPublished = (instrument, meterListener) =>
    {
      if (
        instrument.Meter.Name == EngineMetrics.MeterName
        && instrument.Name == "atli.reports.conversion.duration"
      )
      {
        meterListener.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<double>(
      (_, value, tags, _) =>
      {
        string? outcome = null;
        foreach (var tag in tags)
        {
          if (tag.Key == "outcome")
          {
            outcome = tag.Value as string;
          }
        }

        lock (measurements)
        {
          measurements.Add((value, outcome));
        }
      }
    );
    listener.Start();
    var (converter, _) = CreateConverter(new FakeBrowserProvider(_ => new FakePage()));

    var succeeded = await converter.ConvertAsync("<p>x</p>");
    await succeeded.AsT0.DisposeAsync();
    await converter.ConvertAsync(" ");

    // Other tests may run converters in parallel; look for this test's two outcomes.
    string[] outcomes;
    lock (measurements)
    {
      outcomes = [.. measurements.Select(m => m.Outcome ?? "")];
    }

    await Assert.That(outcomes).Contains("success");
    await Assert.That(outcomes).Contains("InvalidRequest");
  }

  [Test]
  public async Task A_conversion_is_a_span_with_a_child_span_per_stage()
  {
    using var spans = new SpanCollector();
    var (converter, _) = CreateConverter(new FakeBrowserProvider(_ => new FakePage()));

    var result = await converter.ConvertAsync(
      "<p>x</p>",
      new PdfOptions
      {
        PaperSize = PaperSize.A4,
        Orientation = PageOrientation.Landscape,
        GenerateTaggedPdf = false,
      }
    );
    await result.AsT0.DisposeAsync();

    var conversion = spans.Single("atli.reports.convert");
    await Assert.That(conversion.ParentSpanId).IsEqualTo(spans.Root.SpanId);
    await Assert
      .That(spans.ChildrenOf(conversion))
      .IsEquivalentTo(
        [
          "atli.reports.page.open",
          "atli.reports.page.set_content",
          "atli.reports.page.wait",
          "atli.reports.pdf.print",
        ],
        CollectionOrdering.Matching
      );
    await Assert.That(conversion.Status).IsEqualTo(ActivityStatusCode.Unset);
    await Assert.That(conversion.GetTagItem("error.type")).IsNull();
    await Assert.That(conversion.GetTagItem("atli.reports.pdf.paper_size")).IsEqualTo("a4");
    await Assert.That(conversion.GetTagItem("atli.reports.pdf.orientation")).IsEqualTo("landscape");
    await Assert.That((bool)conversion.GetTagItem("atli.reports.pdf.tagged")!).IsFalse();
    await Assert.That((bool)conversion.GetTagItem("atli.reports.pdf.wait_for_signal")!).IsFalse();
    await Assert.That(conversion.GetTagItem("atli.reports.html.length")).IsEqualTo(8);
    await Assert
      .That(conversion.GetTagItem("atli.reports.pdf.size"))
      .IsEqualTo((long)FakePage.Pdf.Length);
    await Assert
      .That(spans.Single("atli.reports.page.wait").GetTagItem("atli.reports.page.wait_for"))
      .IsEqualTo("load");
    await Assert.That(spans.Named("atli.reports.queue.wait")).IsEmpty();
  }

  [Test]
  public async Task No_span_holds_the_html()
  {
    const string secret = "tenant-secret-4711";
    using var spans = new SpanCollector();
    var (converter, _) = CreateConverter(new FakeBrowserProvider(_ => new FakePage()));

    var result = await converter.ConvertAsync(
      $"<p>{secret}</p>",
      new PdfOptions { WaitForSignal = "ready" }
    );
    await result.AsT0.DisposeAsync();

    await Assert.That(spans.Spans).IsNotEmpty();
    foreach (var span in spans.Spans)
    {
      foreach (var tag in span.TagObjects)
      {
        await Assert.That(tag.Value?.ToString() ?? "").DoesNotContain(secret);
      }
    }
  }

  [Test]
  public async Task A_failed_conversion_has_its_kind_as_error_type_and_marks_the_failed_stage()
  {
    using var spans = new SpanCollector();
    FakePage page = new()
    {
      PrintFailure = new DevToolsTimeoutException("Page.printToPDF", TimeSpan.FromSeconds(30)),
    };
    var (converter, _) = CreateConverter(new FakeBrowserProvider(_ => page));

    var result = await converter.ConvertAsync("<p>x</p>");

    var conversion = spans.Single("atli.reports.convert");
    await Assert.That(conversion.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(conversion.StatusDescription).IsEqualTo(result.AsT1.Message);
    await Assert.That(conversion.GetTagItem("error.type")).IsEqualTo("Timeout");
    await Assert.That(conversion.GetTagItem("atli.reports.pdf.size")).IsNull();
    await Assert
      .That(spans.Single("atli.reports.pdf.print").Status)
      .IsEqualTo(ActivityStatusCode.Error);
    await Assert
      .That(spans.Single("atli.reports.page.wait").Status)
      .IsEqualTo(ActivityStatusCode.Unset);
  }

  [Test]
  public async Task A_signal_that_never_comes_fails_the_wait_span()
  {
    using var spans = new SpanCollector();
    FakePage page = new() { SignalArrives = false };
    var (converter, _) = CreateConverter(new FakeBrowserProvider(_ => page));

    await converter.ConvertAsync(
      "<p>x</p>",
      new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TimeSpan.FromSeconds(2) }
    );

    var conversion = spans.Single("atli.reports.convert");
    await Assert.That(conversion.GetTagItem("error.type")).IsEqualTo("SignalTimeout");
    await Assert.That((bool)conversion.GetTagItem("atli.reports.pdf.wait_for_signal")!).IsTrue();
    var wait = spans.Single("atli.reports.page.wait");
    await Assert.That(wait.GetTagItem("atli.reports.page.wait_for")).IsEqualTo("signal");
    await Assert.That(wait.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(spans.Named("atli.reports.pdf.print")).IsEmpty();
  }

  [Test]
  public async Task An_invalid_request_is_a_failed_span_without_stages()
  {
    using var spans = new SpanCollector();
    var (converter, _) = CreateConverter(
      new FakeBrowserProvider(_ => throw new InvalidOperationException("unreachable"))
    );

    await converter.ConvertAsync(" ");

    var conversion = spans.Single("atli.reports.convert");
    await Assert.That(conversion.GetTagItem("error.type")).IsEqualTo("InvalidRequest");
    await Assert.That(conversion.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(spans.ChildrenOf(conversion)).IsEmpty();
  }

  [Test]
  public async Task Only_a_conversion_that_waits_for_its_turn_has_a_queue_span()
  {
    using var spans = new SpanCollector();
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    FakePage blocked = new() { PrintGate = release.Task };
    var (converter, _) = CreateConverter(
      new FakeBrowserProvider(_ => blocked),
      new ReportsEngineConcurrencyOptions { MaxConcurrentConversions = 1, MaxQueueLength = 5 }
    );

    var first = converter.ConvertAsync("<p>first</p>").AsTask();
    await blocked.Printing.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var second = converter.ConvertAsync("<p>second</p>").AsTask();
    await Task.Delay(100);
    release.SetResult();
    await (await first).AsT0.DisposeAsync();
    await (await second).AsT0.DisposeAsync();

    var queued = spans.Single("atli.reports.queue.wait");
    var conversions = spans.Named("atli.reports.convert");
    await Assert.That(conversions.Count).IsEqualTo(2);
    await Assert
      .That(conversions.Count(conversion => conversion.SpanId == queued.ParentSpanId))
      .IsEqualTo(1);
    await Assert.That(queued.Status).IsEqualTo(ActivityStatusCode.Unset);
  }

  [Test]
  [Arguments(8.5, 11, "letter")]
  [Arguments(8.27, 11.69, "a4")]
  [Arguments(8, 10, "8x10in")]
  public async Task Paper_sizes_are_named_or_measured(double width, double height, string expected)
  {
    var described = EngineActivities.DescribePaperSize(
      new PaperSize { Width = width, Height = height }
    );

    await Assert.That(described).IsEqualTo(expected);
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
    ReportsEngineConcurrencyOptions? concurrency = null,
    TimeSpan? conversionTimeout = null
  )
  {
    ConversionHealthTracker tracker = new(TimeProvider.System);
    ReportsEngineOptions options = new()
    {
      ConversionTimeout = conversionTimeout ?? Timeout.InfiniteTimeSpan,
    };
    HtmlToPdfConverter converter = new(
      browsers,
      new ConversionLimiter(concurrency ?? new ReportsEngineConcurrencyOptions()),
      tracker,
      Metrics,
      Options.Create(options),
      NullLogger<HtmlToPdfConverter>.Instance
    );
    return (converter, tracker);
  }

  private static readonly EngineMetrics Metrics = new();

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

    public async Task<long> PrintToPdfAsync(
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

      return Pdf.Length;
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
