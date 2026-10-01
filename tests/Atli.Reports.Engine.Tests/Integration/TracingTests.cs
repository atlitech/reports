using System.Diagnostics;
using Atli.Reports.Engine.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Engine.Tests.Integration;

/// <summary>
/// The spans conversions produce in a real browser.
/// </summary>
[NotInParallel("chrome")]
[ClassDataSource<SharedChromeEngine>(Shared = SharedType.PerTestSession)]
public class TracingTests(SharedChromeEngine engine)
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task A_conversion_in_a_new_browser_traces_the_launch_and_every_stage()
  {
    using var spans = new SpanCollector();
    await using var provider = TestEngine.Create(options =>
      options.Browser.MaxConversionsPerProcess = 1
    );
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();

    var pdf = await converter.ConvertToBytesAsync("<!DOCTYPE html><h1>Traced</h1>");
    await converter.ConvertToBytesAsync("<p>On the replacement</p>");

    var conversions = spans
      .Named("atli.reports.convert")
      .OrderBy(span => span.StartTimeUtc)
      .ToList();
    await Assert.That(conversions.Count).IsEqualTo(2);
    var first = conversions[0];
    await Assert.That(first.Status).IsEqualTo(ActivityStatusCode.Unset);
    await Assert
      .That(spans.ChildrenOf(first))
      .IsEquivalentTo(
        [
          "atli.reports.page.open",
          "atli.reports.page.set_content",
          "atli.reports.page.wait",
          "atli.reports.pdf.print",
        ],
        CollectionOrdering.Matching
      );
    await Assert.That(first.GetTagItem("atli.reports.pdf.size")).IsEqualTo((long)pdf.Length);

    // The first conversion launched the browser, and its lease was the browser's last.
    var open = ChildOf(spans, first, "atli.reports.page.open");
    await Assert
      .That(spans.ChildrenOf(open))
      .IsEquivalentTo(["atli.reports.browser.launch"], CollectionOrdering.Matching);
    await Assert.That(open.GetTagItem("atli.reports.browser.generation")).IsEqualTo(1);
    var recycle = open.Events.Single();
    await Assert.That(recycle.Name).IsEqualTo("atli.reports.browser.recycle");
    await Assert
      .That(recycle.Tags.Single(tag => tag.Key == "atli.reports.browser.recycle.reason").Value)
      .IsEqualTo("max_conversions");

    var launch = spans.Single("atli.reports.browser.launch");
    await Assert.That(launch.GetTagItem("atli.reports.browser.generation")).IsEqualTo(1);
    await Assert.That((int)launch.GetTagItem("atli.reports.browser.pid")!).IsGreaterThan(0);
    await Assert.That(launch.Status).IsEqualTo(ActivityStatusCode.Unset);

    // Printing streams the PDF out of the browser.
    var print = ChildOf(spans, first, "atli.reports.pdf.print");
    await Assert
      .That(spans.ChildrenOf(print))
      .IsEquivalentTo(["atli.reports.pdf.stream"], CollectionOrdering.Matching);
    var stream = ChildOf(spans, print, "atli.reports.pdf.stream");
    await Assert.That(stream.GetTagItem("atli.reports.pdf.size")).IsEqualTo((long)pdf.Length);

    // The replacement launched in the background, in a trace of its own.
    var replacementOpen = ChildOf(spans, conversions[1], "atli.reports.page.open");
    await Assert.That(replacementOpen.GetTagItem("atli.reports.browser.generation")).IsEqualTo(2);
    await Assert.That(spans.ChildrenOf(replacementOpen)).IsEmpty();
  }

  [Test]
  public async Task A_signal_timeout_fails_the_conversion_and_its_wait_span()
  {
    using var spans = new SpanCollector();

    var result = await engine.Converter.ConvertAsync(
      "<p>Never ready</p>",
      new PdfOptions { WaitForSignal = "never", WaitTimeout = TimeSpan.FromMilliseconds(300) },
      TestToken
    );

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.SignalTimeout);
    var conversion = spans.Single("atli.reports.convert");
    await Assert.That(conversion.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(conversion.GetTagItem("error.type")).IsEqualTo("SignalTimeout");
    await Assert
      .That(spans.ChildrenOf(conversion))
      .IsEquivalentTo(
        ["atli.reports.page.open", "atli.reports.page.set_content", "atli.reports.page.wait"],
        CollectionOrdering.Matching
      );
    var wait = spans.Single("atli.reports.page.wait");
    await Assert.That(wait.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(wait.GetTagItem("atli.reports.page.wait_for")).IsEqualTo("signal");
    await Assert
      .That(spans.Single("atli.reports.page.set_content").Status)
      .IsEqualTo(ActivityStatusCode.Unset);
  }

  private static Activity ChildOf(SpanCollector spans, Activity parent, string name) =>
    spans.Spans.Single(span => span.OperationName == name && span.ParentSpanId == parent.SpanId);
}
