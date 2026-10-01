using System.Diagnostics;
using System.Globalization;
using System.Text;
using Atli.Reports.Benchmarks.Load.Infrastructure;
using Atli.Reports.Benchmarks.Load.Results;
using Atli.Reports.Benchmarks.Load.Runner;
using Atli.Reports.Benchmarks.Load.Targets;
using Atli.Reports.Benchmarks.Shared;

namespace Atli.Reports.Benchmarks.Load;

/// <summary>
/// Runs every target × fixture × concurrency cell, one container at a time, and writes the results.
/// </summary>
/// <remarks>
/// Cells are ordered fixture → concurrency → target, and the target order alternates from cell to
/// cell, so slow drifts in background load on the machine hit both targets alike. Every cell starts
/// a fresh container, so a crash or memory growth in one cell cannot leak into the next. Once a
/// level produces no valid PDF for a target and fixture, higher levels are recorded as skipped
/// (unless <see cref="RunOptions.KeepGoing"/>), because each would only wait out client timeouts.
/// </remarks>
internal sealed class BenchmarkRun(RunOptions options)
{
  /// <summary>
  /// The most workers the warm-up uses: enough to start a few browser pages on either target
  /// without queueing a backlog that would spill into the measured phase.
  /// </summary>
  private const int WarmupConcurrencyCap = 4;

  private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(120);
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

  public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
  {
    var fixturesDirectory = BenchmarkFixtures.FindDirectory();
    var repositoryRoot = Path.GetFullPath(Path.Combine(fixturesDirectory, "..", ".."));
    var composeFile =
      options.ComposeFile ?? Path.Combine(repositoryRoot, "benchmarks", "load", "compose.yaml");
    var outputDirectory =
      options.OutputDirectory ?? Path.Combine(repositoryRoot, "benchmarks", "results");
    Directory.CreateDirectory(outputDirectory);

    var targets = options
      .Targets.Select(t =>
        BenchmarkTarget.Create(t, t == "atli" ? options.AtliPort : options.GotenbergPort)
      )
      .ToList();
    Dictionary<string, string> images = new()
    {
      ["atli"] = options.AtliImage,
      ["gotenberg"] = options.GotenbergImage,
    };
    images = images
      .Where(i => targets.Any(t => t.Name == i.Key))
      .ToDictionary(i => i.Key, i => i.Value);

    Dictionary<string, string> composeEnvironment = new()
    {
      ["ATLI_IMAGE"] = options.AtliImage,
      ["GOTENBERG_IMAGE"] = options.GotenbergImage,
      ["BENCH_CPUS"] = options.Cpus,
      ["BENCH_MEMORY"] = options.Memory,
      ["ATLI_PORT"] = options.AtliPort.ToString(Invariant),
      ["GOTENBERG_PORT"] = options.GotenbergPort.ToString(Invariant),
    };
    DockerHost docker = new(composeFile, options.ProjectName, composeEnvironment);
    await docker.DownAsync(cancellationToken);

    var environment = await EnvironmentCollector.CollectAsync(
      repositoryRoot,
      options.ProjectName,
      images
    );
    var name =
      options.Name
      ?? UniqueName(outputDirectory, $"{environment.StartedAt:yyyy-MM-dd}-{environment.GitCommit}");
    var workDirectory = Path.Combine(outputDirectory, name + ".work");
    Directory.CreateDirectory(workDirectory);

    var fixtures = options.Fixtures.Select(BenchmarkFixtures.Get).ToList();
    Dictionary<string, string> html = fixtures.ToDictionary(
      f => f.Name,
      f => BenchmarkFixtures.ReadHtml(f, fixturesDirectory)
    );

    using SocketsHttpHandler handler = new()
    {
      MaxConnectionsPerServer = int.MaxValue,
      ConnectTimeout = TimeSpan.FromSeconds(10),
      PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
      UseCookies = false,
      AutomaticDecompression = System.Net.DecompressionMethods.None,
    };
    using HttpClient client = new(handler, disposeHandler: false)
    {
      Timeout = Timeout.InfiniteTimeSpan,
    };
    LoadRunner runner = new(client);

    Dictionary<string, TargetConfiguration> configurations = [];
    List<CellResult> cells = [];

    // target/fixture → the concurrency level that produced no valid PDF; higher levels are skipped.
    Dictionary<(string Target, string Fixture), int> brokenAt = [];
    var plan = (
      from fixture in fixtures
      from concurrency in options.Concurrency
      select (fixture, concurrency)
    ).ToList();
    var total = plan.Count * targets.Count;
    var index = 0;

    Console.WriteLine(
      $"Running {total} cells into {Path.Combine(outputDirectory, name)}.md (logs: {workDirectory})"
    );
    Console.WriteLine($"Host load average at start: {environment.HostLoadStart}");

    try
    {
      for (var cellGroup = 0; cellGroup < plan.Count; cellGroup++)
      {
        var (fixture, concurrency) = plan[cellGroup];
        var ordered = cellGroup % 2 == 0 ? targets : Enumerable.Reverse(targets).ToList();
        foreach (var target in ordered)
        {
          cancellationToken.ThrowIfCancellationRequested();
          index++;
          Console.Write(
            string.Create(
              Invariant,
              $"[{index, 3}/{total}] {target.Name, -9} {fixture.Name, -10} c={concurrency, -3} "
            )
          );
          if (
            !options.KeepGoing && brokenAt.TryGetValue((target.Name, fixture.Name), out var level)
          )
          {
            CellResult skipped = new()
            {
              Target = target.Name,
              Fixture = fixture.Name,
              Concurrency = concurrency,
              StartedAt = DateTimeOffset.UtcNow,
              IssueSeconds = options.Duration.TotalSeconds,
              Failure = $"skipped: c={level} already produced no valid PDF",
            };
            cells.Add(skipped);
            Console.WriteLine(Describe(skipped));
            continue;
          }

          var request = await target.PrepareAsync(fixture, html[fixture.Name]);
          var cell = await RunCellAsync(
            docker,
            runner,
            target,
            fixture,
            concurrency,
            request,
            workDirectory,
            configurations,
            cancellationToken
          );
          cells.Add(cell);
          Console.WriteLine(Describe(cell));
          if (cell.Succeeded == 0)
          {
            brokenAt.TryAdd((target.Name, fixture.Name), concurrency);
          }
        }
      }
    }
    finally
    {
      await docker.DownAsync(CancellationToken.None);
    }

    environment.FinishedAt = DateTimeOffset.UtcNow;
    environment.HostLoadEnd = await EnvironmentCollector.ReadHostLoadAsync();

    RunResult run = new()
    {
      Name = name,
      Settings = new RunSettings
      {
        Mode = options.Mode,
        Targets = [.. targets.Select(t => t.Name)],
        Fixtures = [.. fixtures.Select(f => f.Name)],
        Concurrency = options.Concurrency,
        WarmupSeconds = options.Warmup.TotalSeconds,
        DurationSeconds = options.Duration.TotalSeconds,
        RequestTimeoutSeconds = options.RequestTimeout.TotalSeconds,
        KeepGoing = options.KeepGoing,
        Cpus = options.Cpus,
        Memory = options.Memory,
        Images = images,
        Note = options.Note,
        TargetConfiguration = configurations,
      },
      Environment = environment,
      Fixtures =
      [
        .. fixtures.Select(f => new FixtureInfo(
          f.Name,
          f.Description,
          Encoding.UTF8.GetByteCount(html[f.Name]),
          f.WaitsForSignal
        )),
      ],
      Cells = cells,
    };

    var basePath = Path.Combine(outputDirectory, name);
    await ReportWriter.WriteAsync(run, basePath + ".md", basePath + ".json", basePath + ".csv");
    Console.WriteLine($"Host load average at end: {environment.HostLoadEnd}");
    Console.WriteLine($"Wrote {basePath}.md, .json and .csv");
    return 0;
  }

  private async Task<CellResult> RunCellAsync(
    DockerHost docker,
    LoadRunner runner,
    BenchmarkTarget target,
    BenchmarkFixture fixture,
    int concurrency,
    PreparedRequest request,
    string workDirectory,
    Dictionary<string, TargetConfiguration> configurations,
    CancellationToken cancellationToken
  )
  {
    CellResult cell = new()
    {
      Target = target.Name,
      Fixture = fixture.Name,
      Concurrency = concurrency,
      StartedAt = DateTimeOffset.UtcNow,
      IssueSeconds = options.Duration.TotalSeconds,
      WarmupConcurrency = Math.Min(concurrency, WarmupConcurrencyCap),
    };
    var cellId = $"{target.Name}-{fixture.Name}-c{concurrency}";
    string? container = null;
    try
    {
      container = await docker.StartAsync(target.Service, cancellationToken);
      var healthy = await WaitHealthyAsync(target, container, cancellationToken);
      if (healthy is not null)
      {
        return cell with
        {
          Failure = healthy,
          ContainerAfter = await DockerHost.InspectStateAsync(container),
        };
      }

      if (!configurations.ContainsKey(target.Name))
      {
        var (env, command) = await DockerHost.InspectConfigAsync(container);
        var browser = (await DockerHost.ExecAsync(container, target.BrowserVersionCommand))?.Trim();
        configurations[target.Name] = new TargetConfiguration(env, command, browser);
      }

      var loadStart = (await DockerHost.ExecAsync(container, "cat /proc/loadavg"))?.Trim();
      await using DockerStatsSampler sampler = new(container);

      // Warm-up: a short closed loop at a capped concurrency, drained before measuring. It starts
      // lazily launched browsers and fills caches without queueing work into the measurement.
      var warmupStart = Stopwatch.GetTimestamp();
      var warmup = await runner.RunAsync(
        new LoadPlan(
          target.BaseAddress,
          request,
          cell.WarmupConcurrency,
          warmupStart + Ticks(options.Warmup),
          options.RequestTimeout
        ),
        cancellationToken
      );

      // Measured phase: every worker starts together, stops starting requests after the duration,
      // and the requests in flight then are awaited, so every request is accounted for.
      var atStart = await ReadCgroupTimedAsync(container);
      var measuredStart = Stopwatch.GetTimestamp();
      var stopIssuing = measuredStart + Ticks(options.Duration);
      var measured = await runner.RunAsync(
        new LoadPlan(target.BaseAddress, request, concurrency, stopIssuing, options.RequestTimeout),
        cancellationToken
      );
      var measuredEnd = measured.Count > 0 ? measured.Max(s => s.End) : Stopwatch.GetTimestamp();
      var atEnd = await ReadCgroupTimedAsync(container);

      var stats = await sampler.StopAsync();
      var state = await DockerHost.InspectStateAsync(container);
      var loadEnd = (await DockerHost.ExecAsync(container, "cat /proc/loadavg"))?.Trim();

      await WriteSamplesAsync(
        Path.Combine(workDirectory, cellId + ".csv"),
        warmupStart,
        warmup,
        measured
      );
      return CellAnalysis.Summarize(
        cell with
        {
          ContainerAfter = state,
          DockerLoadStart = loadStart,
          DockerLoadEnd = loadEnd,
        },
        new CellObservations
        {
          Warmup = warmup,
          Measured = measured,
          MeasuredStart = measuredStart,
          StopIssuing = stopIssuing,
          MeasuredEnd = measuredEnd,
          Stats = stats,
          CgroupAtStart = atStart,
          CgroupAtEnd = atEnd,
        }
      );
    }
    catch (InvalidOperationException exception)
    {
      return cell with { Failure = exception.Message };
    }
    finally
    {
      if (container is not null)
      {
        await DockerHost.SaveLogsAsync(container, Path.Combine(workDirectory, cellId + ".log"));
        await ProcessRunner.RunAsync(
          "docker",
          ["rm", "--force", "--volumes", container],
          cancellationToken: CancellationToken.None
        );
      }
    }
  }

  /// <summary>
  /// Polls the target's health endpoint; returns <see langword="null"/> once it answers 200, or why not.
  /// </summary>
  private static async Task<string?> WaitHealthyAsync(
    BenchmarkTarget target,
    string container,
    CancellationToken cancellationToken
  )
  {
    using HttpClient probe = new() { Timeout = TimeSpan.FromSeconds(5) };
    var deadline = Stopwatch.GetTimestamp() + Ticks(HealthTimeout);
    while (Stopwatch.GetTimestamp() < deadline)
    {
      try
      {
        using var response = await probe.GetAsync(target.HealthUri, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
          return null;
        }
      }
      catch (HttpRequestException)
      {
        // Not listening yet.
      }
      catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        // Probe timed out; try again.
      }

      if (await DockerHost.InspectStateAsync(container) is { Running: false } state)
      {
        return $"container exited (code {state.ExitCode}) before becoming healthy";
      }

      await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
    }

    return $"not healthy after {HealthTimeout.TotalSeconds:0} s";
  }

  private static async Task WriteSamplesAsync(
    string path,
    long origin,
    IReadOnlyList<RequestSample> warmup,
    IReadOnlyList<RequestSample> measured
  )
  {
    StringBuilder csv = new("phase,start_ms,end_ms,latency_ms,outcome,pages,bytes,detail\n");
    foreach (var (phase, samples) in new[] { ("warmup", warmup), ("measured", measured) })
    {
      foreach (var sample in samples)
      {
        var detail = sample.Detail is null
          ? ""
          : "\"" + sample.Detail.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        csv.Append(
          Invariant,
          $"{phase},{Stopwatch.GetElapsedTime(origin, sample.Start).TotalMilliseconds:0.0},{Stopwatch.GetElapsedTime(origin, sample.End).TotalMilliseconds:0.0},{sample.LatencyMilliseconds:0.0},{sample.Outcome},{sample.Pages},{sample.Bytes},{detail}\n"
        );
      }
    }

    await File.WriteAllTextAsync(path, csv.ToString());
  }

  private static string Describe(CellResult cell)
  {
    if (cell.Failure is not null)
    {
      return cell.Failure.StartsWith("skipped", StringComparison.Ordinal)
        ? cell.Failure
        : $"FAILED: {cell.Failure}";
    }

    var errors = cell.Errors.Values.Sum();
    return string.Create(
      Invariant,
      $"{cell.DocsPerSecond, 7:0.00} docs/s  p50 {cell.Latency?.P50, 8:0} ms  p95 {cell.Latency?.P95, 8:0} ms  ok {cell.Succeeded}/{cell.Requests}  errors {errors}  peak mem {ReportWriter.FormatBytes(cell.MemoryPeakSampledBytes)}"
    );
  }

  /// <summary>
  /// Reads the cgroup counters and stamps them with the middle of the <c>docker exec</c> call, which
  /// can take a while when the container is under memory pressure.
  /// </summary>
  private static async Task<TimedCounters> ReadCgroupTimedAsync(string container)
  {
    var before = Stopwatch.GetTimestamp();
    var counters = await DockerHost.ReadCgroupAsync(container);
    var after = Stopwatch.GetTimestamp();
    return new TimedCounters(before + ((after - before) / 2), counters);
  }

  private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

  private static string UniqueName(string directory, string baseName)
  {
    var name = baseName;
    for (var suffix = 2; File.Exists(Path.Combine(directory, name + ".md")); suffix++)
    {
      name = $"{baseName}-{suffix}";
    }

    return name;
  }
}
