using System.Collections.Concurrent;
using System.Diagnostics;
using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Tests.Integration;

/// <summary>
/// The background retries after a failed browser launch, over fake browsers that cannot start:
/// backoff, telemetry, one launch at a time, and shutdown. Each test owns its engine.
/// </summary>
public class LaunchRetryTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task A_failed_warm_up_is_retried_with_backoff_and_each_attempt_is_traced_and_logged()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    // The browser's output identifies this test's launch spans among other tests' spans.
    var marker = Guid.NewGuid().ToString("N");
    using var fake = FakeBrowser.Create($"echo 'missing library {marker}' >&2; exit 127");
    using LaunchSpans launches = new(marker);
    LogCollector logs = new();
    await using var provider = TestEngine.Create(
      options =>
      {
        options.Browser.ExecutablePath = fake.Path;
        options.Browser.WarmUpOnStartup = true;
        options.Browser.LaunchRetryDelay = TimeSpan.FromMilliseconds(50);
        options.Browser.MaxLaunchRetryDelay = TimeSpan.FromMilliseconds(200);
      },
      services => services.AddLogging(logging => logging.AddProvider(logs))
    );
    var browsers = provider.GetRequiredService<BrowserManager>();

    await StartAsync(provider);

    await Assert
      .That(
        await TestEngine.EventuallyAsync(
          () => Task.FromResult(logs.WithEventId(309).Count >= 4 && launches.Count >= 4),
          TestEngine.GenerousTimeout
        )
      )
      .IsTrue()
      .Because("the engine keeps retrying a browser that cannot start");

    var status = browsers.GetStatus();
    await Assert.That(status.LaunchFailure).Contains("exited with code 127");
    await Assert.That(status.FailedLaunches).IsGreaterThanOrEqualTo(4);
    await Assert.That(status.Retrying).IsTrue();
    await Assert.That(browsers.Current).IsNull();

    // 50 ms, doubling, capped at 200 ms.
    var retries = logs.WithEventId(309).Take(4).ToList();
    await Assert
      .That(string.Join(", ", retries.Select(entry => entry["Delay"])))
      .IsEqualTo("00:00:00.0500000, 00:00:00.1000000, 00:00:00.2000000, 00:00:00.2000000");
    await Assert
      .That(string.Join(", ", retries.Select(entry => entry["Attempt"])))
      .IsEqualTo("2, 3, 4, 5");
    await Assert.That(retries.All(entry => entry.Level == LogLevel.Information)).IsTrue();
    var failures = logs.WithEventId(301);
    await Assert.That(failures.Count).IsGreaterThanOrEqualTo(4);
    await Assert.That(failures.All(entry => entry.Level == LogLevel.Error)).IsTrue();
    await Assert.That(logs.WithEventId(307)).HasSingleItem().Because("warm-up failed once");

    var traced = launches.Ordered.Take(4).ToList();
    await Assert
      .That(
        string.Join(
          ", ",
          traced.Select(span => span.GetTagItem("atli.reports.browser.launch.attempt"))
        )
      )
      .IsEqualTo("1, 2, 3, 4");
    foreach (var span in traced)
    {
      await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
      await Assert
        .That(span.GetTagItem("error.type"))
        .IsEqualTo(typeof(BrowserUnavailableException).FullName);
    }

    foreach (var retry in traced.Skip(1))
    {
      await Assert
        .That(retry.ParentSpanId)
        .IsEqualTo(default(ActivitySpanId))
        .Because("a retry is a trace of its own");
    }
  }

  [Test]
  public async Task An_exit_before_output_pump_registration_preserves_startup_diagnostics()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    var marker = Guid.NewGuid().ToString("N");
    using var fake = FakeBrowser.Create($"echo 'missing library {marker}' >&2; exit 127");
    var error = await Assert
      .That(async () =>
        await BrowserProcess.LaunchAsync(
          new ReportsEngineBrowserOptions { ExecutablePath = fake.Path },
          new ExitBeforeOutputLogger(),
          TestToken
        )
      )
      .Throws<BrowserUnavailableException>();

    await Assert.That(error!.Message).Contains("exited with code 127").And.Contains(marker);
  }

  [Test]
  public async Task Retries_and_conversions_never_launch_two_browsers_at_once()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    // mkdir is atomic: a second launch while one is still starting finds the directory there.
    using var fake = FakeBrowser.Create(
      """
      here="$(dirname "$0")"
      mkdir "$here/starting" 2>/dev/null || echo overlap >> "$here/overlaps"
      sleep 0.1
      rmdir "$here/starting"
      exit 3
      """
    );
    await using var provider = TestEngine.Create(options =>
    {
      options.Browser.ExecutablePath = fake.Path;
      options.Browser.WarmUpOnStartup = true;
      options.Browser.LaunchRetryDelay = TimeSpan.FromMilliseconds(10);
      options.Browser.MaxLaunchRetryDelay = TimeSpan.FromMilliseconds(10);
    });
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();

    await StartAsync(provider);
    var results = await Task.WhenAll(
      Enumerable
        .Range(0, 4)
        .Select(async _ =>
        {
          List<ConversionErrorKind?> kinds = [];
          for (var i = 0; i < 5; i++)
          {
            var result = await converter.ConvertAsync("<p>x</p>", cancellationToken: TestToken);
            kinds.Add(result.IsT1 ? result.AsT1.Kind : null);
          }

          return kinds;
        })
    );

    await Assert
      .That(
        results
          .SelectMany(kinds => kinds)
          .All(kind => kind == ConversionErrorKind.BrowserUnavailable)
      )
      .IsTrue();
    await Assert.That(fake.CountLaunches()).IsGreaterThanOrEqualTo(5);
    await Assert
      .That(File.Exists(Path.Combine(fake.Directory, "overlaps")))
      .IsFalse()
      .Because("a retry waits for a conversion's launch and the other way round");
  }

  [Test]
  public async Task Shutdown_stops_the_retries()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create("exit 3");
    await using var provider = TestEngine.Create(options =>
    {
      options.Browser.ExecutablePath = fake.Path;
      options.Browser.WarmUpOnStartup = true;
      options.Browser.LaunchRetryDelay = TimeSpan.FromMilliseconds(20);
      options.Browser.MaxLaunchRetryDelay = TimeSpan.FromMilliseconds(20);
    });
    var browsers = provider.GetRequiredService<BrowserManager>();
    await StartAsync(provider);
    await Assert
      .That(
        await TestEngine.EventuallyAsync(
          () => Task.FromResult(fake.CountLaunches() >= 3),
          TestEngine.GenerousTimeout
        )
      )
      .IsTrue();

    var stopwatch = Stopwatch.StartNew();
    await StopAsync(provider);
    stopwatch.Stop();
    var launched = fake.CountLaunches();
    await Task.Delay(TimeSpan.FromMilliseconds(500), TestToken);

    await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    await Assert
      .That(fake.CountLaunches())
      .IsEqualTo(launched)
      .Because("nothing launches after shutdown");
    await Assert.That(browsers.GetStatus().Retrying).IsFalse();
  }

  [Test]
  public async Task Shutdown_does_not_wait_out_the_retry_delay()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create("exit 3");
    await using var provider = TestEngine.Create(options =>
    {
      options.Browser.ExecutablePath = fake.Path;
      options.Browser.WarmUpOnStartup = true;
      options.Browser.LaunchRetryDelay = TimeSpan.FromHours(1);
    });
    var browsers = provider.GetRequiredService<BrowserManager>();
    await StartAsync(provider);
    await Assert.That(browsers.GetStatus().Retrying).IsTrue();

    var stopwatch = Stopwatch.StartNew();
    await StopAsync(provider);

    await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    await Assert.That(browsers.GetStatus().Retrying).IsFalse();
    await Assert.That(fake.CountLaunches()).IsEqualTo(1);
  }

  private static async Task StartAsync(IServiceProvider provider)
  {
    foreach (var service in provider.GetServices<IHostedService>())
    {
      await service.StartAsync(TestToken);
    }
  }

  private static async Task StopAsync(IServiceProvider provider)
  {
    foreach (var service in provider.GetServices<IHostedService>().Reverse())
    {
      await service.StopAsync(TestToken);
    }
  }

  /// <summary>
  /// Holds the startup log before the pumps are registered, reproducing a fast-exit/slow-logger
  /// schedule. Waiting for process exit is deterministic; the short delay lets its queued event run.
  /// </summary>
  private sealed class ExitBeforeOutputLogger : ILogger
  {
    public IDisposable? BeginScope<TState>(TState state)
      where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter
    )
    {
      if (eventId.Id != 200)
      {
        return;
      }

      var values = (IReadOnlyList<KeyValuePair<string, object?>>)(object)state!;
      var processId = (int)values.Single(value => value.Key == "ProcessId").Value!;
      try
      {
        using var process = Process.GetProcessById(processId);
        if (!process.WaitForExit(5000))
        {
          throw new InvalidOperationException("The fake browser did not exit.");
        }
      }
      catch (ArgumentException)
      {
        // The fake exited before the log callback could open it.
      }
      Thread.Sleep(100);
    }
  }

  /// <summary>
  /// The failed <c>atli.reports.browser.launch</c> spans whose error mentions a marker, whatever
  /// trace they are in.
  /// </summary>
  private sealed class LaunchSpans : IDisposable
  {
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ActivityListener _listener;

    public LaunchSpans(string marker)
    {
      _listener = new ActivityListener
      {
        ShouldListenTo = source => source.Name == ReportsEngineTelemetry.ActivitySourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
          ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = span =>
        {
          if (
            span.OperationName == "atli.reports.browser.launch"
            && span.StatusDescription?.Contains(marker, StringComparison.Ordinal) == true
          )
          {
            _spans.Enqueue(span);
          }
        },
      };
      ActivitySource.AddActivityListener(_listener);
    }

    public int Count => _spans.Count;

    public IEnumerable<Activity> Ordered => _spans.OrderBy(span => span.StartTimeUtc);

    public void Dispose() => _listener.Dispose();
  }
}
