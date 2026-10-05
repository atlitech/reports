using System.Diagnostics;
using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Atli.Reports.Engine.Tests.Integration;

/// <summary>
/// The long-lived browser: crash recovery, recycling, and clean shutdown. Each test owns its engine.
/// </summary>
[NotInParallel("chrome")]
public class BrowserLifecycleTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Conversions_share_one_browser_process()
  {
    await using var provider = TestEngine.Create();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    var browsers = provider.GetRequiredService<BrowserManager>();

    await converter.ConvertToBytesAsync("<p>One</p>");
    var first = browsers.Current!.ProcessId;
    await converter.ConvertToBytesAsync("<p>Two</p>");

    await Assert.That(browsers.Current!.ProcessId).IsEqualTo(first);
    await Assert.That(browsers.Launches).IsEqualTo(1);
  }

  [Test]
  public async Task After_the_browser_is_killed_the_next_conversion_succeeds()
  {
    await using var provider = TestEngine.Create();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    var browsers = provider.GetRequiredService<BrowserManager>();
    await converter.ConvertToBytesAsync("<p>Before</p>");
    var killed = browsers.Current!;

    Process.GetProcessById(killed.ProcessId).Kill(entireProcessTree: true);
    var pdf = await converter.ConvertToBytesAsync("<p>After</p>");

    await Assert.That(PdfInspector.HasPdfHeader(pdf)).IsTrue();
    await Assert.That(browsers.Current!.ProcessId).IsNotEqualTo(killed.ProcessId);
    await Assert
      .That(
        await TestEngine.EventuallyAsync(
          () => Task.FromResult(!Directory.Exists(killed.ProfileDirectory)),
          TestEngine.GenerousTimeout
        )
      )
      .IsTrue()
      .Because("the dead browser's profile directory is deleted");
  }

  [Test]
  public async Task A_conversion_running_when_the_browser_dies_is_BrowserUnavailable()
  {
    await using var provider = TestEngine.Create();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    var browsers = provider.GetRequiredService<BrowserManager>();
    await converter.ConvertToBytesAsync("<p>Start the browser</p>");
    await using var probe = await DevToolsProbe.ConnectAsync(browsers.Current!);

    var running = converter
      .ConvertAsync(
        "<title>in-flight</title><p>Never ready</p>",
        new PdfOptions { WaitForSignal = "never", WaitTimeout = TimeSpan.FromMinutes(1) },
        TestToken
      )
      .AsTask();
    await probe.WaitForPageAsync("in-flight", TestEngine.GenerousTimeout);
    var stopwatch = Stopwatch.StartNew();
    Process.GetProcessById(browsers.Current!.ProcessId).Kill(entireProcessTree: true);
    var result = await running;

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(15));
  }

  [Test]
  public async Task A_crashed_page_fails_fast_instead_of_waiting_for_its_signal()
  {
    await using var provider = TestEngine.Create();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    var browsers = provider.GetRequiredService<BrowserManager>();
    await converter.ConvertToBytesAsync("<p>Start the browser</p>");
    await using var probe = await DevToolsProbe.ConnectAsync(browsers.Current!);

    var running = converter
      .ConvertAsync(
        "<title>doomed</title><p>Never ready</p>",
        new PdfOptions { WaitForSignal = "never", WaitTimeout = TimeSpan.FromMinutes(1) },
        TestToken
      )
      .AsTask();
    var target = await probe.WaitForPageAsync("doomed", TestEngine.GenerousTimeout);
    var stopwatch = Stopwatch.StartNew();
    await probe.CrashAsync(target);
    var result = await running;
    var next = await converter.ConvertToBytesAsync("<p>Next</p>");

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(15));
    await Assert.That(PdfInspector.HasPdfHeader(next)).IsTrue();
    await Assert.That(browsers.Launches).IsEqualTo(1).Because("a page crash leaves the browser up");
  }

  [Test]
  public async Task The_browser_is_recycled_after_its_maximum_conversions()
  {
    await using var provider = TestEngine.Create(options =>
      options.Browser.MaxConversionsPerProcess = 2
    );
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    var browsers = provider.GetRequiredService<BrowserManager>();

    await converter.ConvertToBytesAsync("<p>1</p>");
    var first = browsers.Current!;
    await converter.ConvertToBytesAsync("<p>2</p>");
    await converter.ConvertToBytesAsync("<p>3</p>");

    await Assert.That(browsers.Launches).IsEqualTo(2);
    await Assert.That(browsers.Current!.ProcessId).IsNotEqualTo(first.ProcessId);
    await Assert
      .That(await ProcessAndProfileGoneAsync(first))
      .IsTrue()
      .Because("the recycled browser is killed and its profile deleted");
  }

  [Test]
  public async Task Recycling_lets_running_conversions_finish_on_the_old_browser()
  {
    await using var provider = TestEngine.Create(options =>
    {
      options.Browser.MaxConversionsPerProcess = 1;
      options.Network.Mode = ReportsEngineNetworkMode.Unrestricted;
    });
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    var browsers = provider.GetRequiredService<BrowserManager>();
    await browsers.WarmUpAsync(TestToken);
    var first = browsers.Current!;
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using TestHttpServer server = new();
    server.Map("/gate", "go", "text/plain", gate: release.Task);

    // The first conversion is the first browser's last: it waits on a request the test holds back,
    // while the next conversion goes to the replacement browser.
    var held = converter
      .ConvertAsync(
        $$"""
        <script>fetch('{{server.BaseUrl}}/gate', { mode: 'no-cors' }).then(function () { window.pdfReady(); });</script>
        """,
        new PdfOptions { WaitForSignal = "pdfReady", WaitTimeout = TimeSpan.FromMinutes(1) },
        TestToken
      )
      .AsTask();
    await Assert
      .That(
        await TestEngine.EventuallyAsync(
          () => Task.FromResult(server.Requests.ContainsKey("/gate")),
          TestEngine.GenerousTimeout
        )
      )
      .IsTrue();

    var next = await converter.ConvertToBytesAsync("<p>Next</p>");
    await Assert.That(held.IsCompleted).IsFalse();
    await Assert.That(browsers.Launches).IsGreaterThanOrEqualTo(2);
    await Assert.That(ProcessExited(first.ProcessId)).IsFalse().Because("it still has work");

    release.SetResult();
    var heldResult = await held;

    await Assert.That(PdfInspector.HasPdfHeader(next)).IsTrue();
    await Assert.That(heldResult.IsT0).IsTrue().Because(TestEngine.Describe(heldResult.Value));
    await heldResult.AsT0.DisposeAsync();
    await Assert
      .That(await ProcessAndProfileGoneAsync(first))
      .IsTrue()
      .Because("the old browser closes once its last conversion is done");
  }

  [Test]
  public async Task An_idle_browser_is_closed_and_the_next_conversion_starts_another()
  {
    await using var provider = TestEngine.Create(options =>
      options.Browser.IdleTimeout = TimeSpan.FromMilliseconds(500)
    );
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    var browsers = provider.GetRequiredService<BrowserManager>();

    await converter.ConvertToBytesAsync("<p>Busy</p>");
    var idle = browsers.Current!;
    await Assert.That(await ProcessAndProfileGoneAsync(idle)).IsTrue();
    await Assert.That(browsers.Current).IsNull();

    var pdf = await converter.ConvertToBytesAsync("<p>Busy again</p>");

    await Assert.That(PdfInspector.HasPdfHeader(pdf)).IsTrue();
    await Assert.That(browsers.Launches).IsEqualTo(2);
  }

  [Test]
  public async Task Disposing_the_engine_kills_the_browser_and_deletes_its_profile()
  {
    var provider = TestEngine.Create();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();
    var browsers = provider.GetRequiredService<BrowserManager>();
    await converter.ConvertToBytesAsync("<p>Then dispose</p>");
    var browser = browsers.Current!;
    await Assert.That(Directory.Exists(browser.ProfileDirectory)).IsTrue();
    await Assert.That(await TestEngine.FindProcessesAsync(browser.ProfileDirectory)).IsNotEmpty();

    await provider.DisposeAsync();

    await Assert.That(await TestEngine.FindProcessesAsync(browser.ProfileDirectory)).IsEmpty();
    await Assert.That(Directory.Exists(browser.ProfileDirectory)).IsFalse();
    await Assert.That(ProcessExited(browser.ProcessId)).IsTrue();

    var after = await converter.ConvertAsync("<p>Too late</p>", cancellationToken: TestToken);
    await Assert.That(after.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
  }

  [Test]
  public async Task Synchronous_disposal_also_cleans_up()
  {
    var provider = TestEngine.Create();
    await provider.GetRequiredService<IHtmlToPdfConverter>().ConvertToBytesAsync("<p>x</p>");
    var browser = provider.GetRequiredService<BrowserManager>().Current!;

    provider.Dispose();

    await Assert.That(await TestEngine.FindProcessesAsync(browser.ProfileDirectory)).IsEmpty();
    await Assert.That(Directory.Exists(browser.ProfileDirectory)).IsFalse();
  }

  [Test]
  public async Task The_host_warms_the_browser_up_and_closes_it_on_stop()
  {
    await using var provider = TestEngine.Create(options => options.Browser.WarmUpOnStartup = true);
    var browsers = provider.GetRequiredService<BrowserManager>();
    var hostedServices = provider.GetServices<IHostedService>().ToList();

    foreach (var service in hostedServices)
    {
      await service.StartAsync(TestToken);
    }

    var browser = browsers.Current;
    await Assert
      .That(browser)
      .IsNotNull()
      .Because("warm-up starts the browser before any conversion");

    foreach (var service in hostedServices)
    {
      await service.StopAsync(TestToken);
    }

    await Assert.That(await ProcessAndProfileGoneAsync(browser!)).IsTrue();
  }

  [Test]
  public async Task A_browser_that_never_reports_its_endpoint_is_killed_and_cleaned_up()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create("while true; do sleep 1; done");
    await using var provider = TestEngine.Create(options =>
    {
      options.Browser.ExecutablePath = fake.Path;
      options.Browser.StartupTimeout = TimeSpan.FromSeconds(2);
    });

    var result = await provider
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync("<p>x</p>", cancellationToken: TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert.That(result.AsT1.Message).Contains("did not report its DevTools endpoint");
    var profile = await fake.ReadProfileDirectoryAsync();
    await Assert.That(await TestEngine.FindProcessesAsync(profile)).IsEmpty();
    await Assert.That(Directory.Exists(profile)).IsFalse();
  }

  [Test]
  public async Task A_browser_that_exits_at_once_is_reported_with_its_output()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create("echo 'Missing X server or $DISPLAY' >&2; exit 3");
    await using var provider = TestEngine.Create(options =>
      options.Browser.ExecutablePath = fake.Path
    );

    var result = await provider
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync("<p>x</p>", cancellationToken: TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert.That(result.AsT1.Message).Contains("exited with code 3");
    await Assert.That(result.AsT1.Message).Contains("Missing X server");
    await Assert.That(Directory.Exists(await fake.ReadProfileDirectoryAsync())).IsFalse();
  }

  [Test]
  public async Task A_browser_that_fails_to_start_fails_its_launch_span()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create("exit 3");
    using var spans = new SpanCollector();
    await using var provider = TestEngine.Create(options =>
      options.Browser.ExecutablePath = fake.Path
    );

    var result = await provider
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync("<p>x</p>", cancellationToken: TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    var conversion = spans.Single("atli.reports.convert");
    await Assert.That(conversion.GetTagItem("error.type")).IsEqualTo("BrowserUnavailable");
    var open = spans.Single("atli.reports.page.open");
    await Assert.That(open.Status).IsEqualTo(ActivityStatusCode.Error);
    var launch = spans.Single("atli.reports.browser.launch");
    await Assert.That(launch.ParentSpanId).IsEqualTo(open.SpanId);
    await Assert.That(launch.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert
      .That(launch.GetTagItem("error.type"))
      .IsEqualTo(typeof(BrowserUnavailableException).FullName);
    await Assert.That(launch.Events.Select(e => e.Name)).Contains("exception");
  }

  private static async Task<bool> ProcessAndProfileGoneAsync(BrowserInstance browser) =>
    await TestEngine.EventuallyAsync(
      async () =>
        ProcessExited(browser.ProcessId)
        && !Directory.Exists(browser.ProfileDirectory)
        && (await TestEngine.FindProcessesAsync(browser.ProfileDirectory)).Count == 0,
      TestEngine.GenerousTimeout
    );

  private static bool ProcessExited(int processId)
  {
    try
    {
      using var process = Process.GetProcessById(processId);
      return process.HasExited;
    }
    catch (ArgumentException)
    {
      return true;
    }
  }
}
