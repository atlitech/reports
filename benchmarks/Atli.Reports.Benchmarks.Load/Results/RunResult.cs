using System.Text.Json.Serialization;
using Atli.Reports.Benchmarks.Load.Infrastructure;

namespace Atli.Reports.Benchmarks.Load.Results;

/// <summary>
/// Everything one <c>run</c> produced; serialized as the raw JSON next to the Markdown report.
/// </summary>
internal sealed record RunResult
{
  public required string Name { get; init; }
  public required RunSettings Settings { get; init; }
  public required EnvironmentInfo Environment { get; init; }
  public required IReadOnlyList<FixtureInfo> Fixtures { get; init; }
  public required IReadOnlyList<CellResult> Cells { get; init; }
}

internal sealed record RunSettings
{
  public required string Mode { get; init; }
  public required IReadOnlyList<string> Targets { get; init; }
  public required IReadOnlyList<string> Fixtures { get; init; }
  public required IReadOnlyList<int> Concurrency { get; init; }
  public required double WarmupSeconds { get; init; }
  public required double DurationSeconds { get; init; }
  public required double RequestTimeoutSeconds { get; init; }

  /// <summary>
  /// Whether levels above one that produced no valid PDF were still measured.
  /// </summary>
  public bool KeepGoing { get; init; }
  public required string Cpus { get; init; }
  public required string Memory { get; init; }
  public required IReadOnlyDictionary<string, string> Images { get; init; }
  public string? Note { get; init; }

  /// <summary>
  /// The environment and command line each target container ran with, as Docker reports them.
  /// </summary>
  public IReadOnlyDictionary<string, TargetConfiguration> TargetConfiguration { get; init; } =
    new Dictionary<string, TargetConfiguration>();
}

internal sealed record TargetConfiguration(
  IReadOnlyList<string> Environment,
  IReadOnlyList<string> Command,
  string? BrowserVersion
);

internal sealed record FixtureInfo(
  string Name,
  string Description,
  long HtmlBytes,
  bool WaitsForSignal
);

/// <summary>
/// Latency percentiles in milliseconds (nearest-rank).
/// </summary>
internal sealed record LatencySummary(
  int Count,
  double Min,
  double Mean,
  double P50,
  double P90,
  double P95,
  double P99,
  double Max
);

/// <summary>
/// One target × fixture × concurrency measurement.
/// </summary>
internal sealed record CellResult
{
  public required string Target { get; init; }
  public required string Fixture { get; init; }
  public required int Concurrency { get; init; }
  public required DateTimeOffset StartedAt { get; init; }

  /// <summary>
  /// How long workers kept starting requests in the measured phase.
  /// </summary>
  public required double IssueSeconds { get; init; }

  /// <summary>
  /// The measured phase's wall time: from the first request until the last one finished, so requests
  /// still in flight when issuing stopped are included.
  /// </summary>
  public double MeasuredSeconds { get; init; }

  /// <summary>
  /// Workers during the warm-up: the cell's concurrency, capped (see the README).
  /// </summary>
  public int WarmupConcurrency { get; init; }

  public int WarmupRequests { get; init; }
  public int WarmupFailures { get; init; }

  /// <summary>
  /// Requests sent in the measured phase; each ran to completion or to the client timeout.
  /// </summary>
  public int Requests { get; init; }

  /// <summary>
  /// Of <see cref="Requests"/>, those that returned a complete PDF.
  /// </summary>
  public int Succeeded { get; init; }

  /// <summary>
  /// <see cref="Succeeded"/> divided by <see cref="MeasuredSeconds"/>.
  /// </summary>
  public double DocsPerSecond { get; init; }

  /// <summary>
  /// How long requests still in flight when issuing stopped took to finish.
  /// </summary>
  public double DrainSeconds { get; init; }

  /// <summary>
  /// Latency of the successful requests among <see cref="Requests"/>.
  /// </summary>
  public LatencySummary? Latency { get; init; }

  /// <summary>
  /// Latency of every request among <see cref="Requests"/>, failures included.
  /// </summary>
  public LatencySummary? LatencyAll { get; init; }

  /// <summary>
  /// Failed requests among <see cref="Requests"/>, by outcome class.
  /// </summary>
  public IReadOnlyDictionary<string, int> Errors { get; init; } = new Dictionary<string, int>();

  /// <summary>
  /// The first error body or exception message seen for each outcome class (measured or warm-up).
  /// </summary>
  public IReadOnlyDictionary<string, string> ErrorDetails { get; init; } =
    new Dictionary<string, string>();

  /// <summary>
  /// How many successful requests produced each page count.
  /// </summary>
  public IReadOnlyDictionary<string, int> PageCounts { get; init; } = new Dictionary<string, int>();

  public long? MedianPdfBytes { get; init; }

  /// <summary>
  /// Average CPU over the measured phase, in cores, from the container's cgroup <c>cpu.stat</c>.
  /// </summary>
  public double? CpuCoresAverage { get; init; }

  /// <summary>
  /// Container CPU time per valid PDF over the measured phase, in seconds: the cost of one document,
  /// and much less sensitive to other load on the machine than throughput or latency.
  /// </summary>
  public double? CpuSecondsPerDoc { get; init; }

  /// <summary>
  /// The highest <c>docker stats</c> CPU reading during the measured phase, in percent of one core.
  /// </summary>
  public double? CpuPercentPeakSampled { get; init; }

  /// <summary>
  /// The highest <c>docker stats</c> memory reading during the cell (usage minus inactive page cache).
  /// </summary>
  public long? MemoryPeakSampledBytes { get; init; }

  /// <summary>
  /// The cgroup <c>memory.peak</c> over the container's life (this cell only; page cache included).
  /// </summary>
  public long? MemoryPeakCgroupBytes { get; init; }

  public int? OomKills { get; init; }
  public ContainerState? ContainerAfter { get; init; }

  /// <summary>
  /// The Docker host's <c>/proc/loadavg</c> when the cell started and ended (on Docker Desktop or
  /// OrbStack, the Linux VM's load).
  /// </summary>
  public string? DockerLoadStart { get; init; }

  public string? DockerLoadEnd { get; init; }

  /// <summary>
  /// Why the cell has no measurement, when it has none.
  /// </summary>
  public string? Failure { get; init; }
}

internal sealed record EnvironmentInfo
{
  public required DateTimeOffset StartedAt { get; init; }
  public DateTimeOffset FinishedAt { get; set; }
  public required string GitCommit { get; init; }
  public required string GitBranch { get; init; }
  public required bool GitDirty { get; init; }

  /// <summary>
  /// The last commit that changed the engine or server sources, i.e. the code under test.
  /// </summary>
  public required string EngineCommit { get; init; }

  public required string HostOs { get; init; }
  public required string HostCpu { get; init; }
  public required int HostLogicalCores { get; init; }
  public required string HostMemory { get; init; }
  public required string HostLoadStart { get; init; }
  public string HostLoadEnd { get; set; } = "";
  public required string DotnetRuntime { get; init; }
  public required string DockerServer { get; init; }
  public required string DockerCompose { get; init; }

  /// <summary>
  /// Containers that were running on the Docker host besides the benchmark target (noise sources).
  /// </summary>
  public required IReadOnlyList<string> OtherContainers { get; init; }

  public required IReadOnlyDictionary<string, string> ImageIds { get; init; }
}

[JsonSourceGenerationOptions(
  WriteIndented = true,
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(RunResult))]
internal sealed partial class ResultsJsonContext : JsonSerializerContext;
