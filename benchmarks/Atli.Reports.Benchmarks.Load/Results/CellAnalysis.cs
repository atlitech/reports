using System.Diagnostics;
using System.Globalization;
using Atli.Reports.Benchmarks.Load.Infrastructure;
using Atli.Reports.Benchmarks.Load.Runner;

namespace Atli.Reports.Benchmarks.Load.Results;

/// <summary>
/// The raw observations of one cell, before they are summarized.
/// </summary>
internal sealed record CellObservations
{
  public required IReadOnlyList<RequestSample> Warmup { get; init; }
  public required IReadOnlyList<RequestSample> Measured { get; init; }

  /// <summary>
  /// When the measured phase's workers started.
  /// </summary>
  public required long MeasuredStart { get; init; }

  /// <summary>
  /// When the measured phase's workers stopped starting new requests.
  /// </summary>
  public required long StopIssuing { get; init; }

  /// <summary>
  /// When the last measured request finished.
  /// </summary>
  public required long MeasuredEnd { get; init; }

  public required IReadOnlyList<StatsSample> Stats { get; init; }
  public required TimedCounters CgroupAtStart { get; init; }
  public required TimedCounters CgroupAtEnd { get; init; }
}

/// <summary>
/// Cgroup counters and when they were read (<see cref="Stopwatch.GetTimestamp"/>).
/// </summary>
internal sealed record TimedCounters(long Timestamp, CgroupCounters Counters);

/// <summary>
/// Turns the samples of a cell into the numbers the report shows.
/// </summary>
internal static class CellAnalysis
{
  public static CellResult Summarize(CellResult cell, CellObservations observations)
  {
    var measuredSeconds = Stopwatch
      .GetElapsedTime(observations.MeasuredStart, observations.MeasuredEnd)
      .TotalSeconds;
    var measured = observations.Measured;
    var succeeded = measured.Where(s => s.Succeeded).ToList();
    var phaseStats = observations
      .Stats.Where(s =>
        s.Timestamp >= observations.MeasuredStart && s.Timestamp <= observations.MeasuredEnd
      )
      .ToList();

    return cell with
    {
      MeasuredSeconds = Math.Round(measuredSeconds, 3),
      WarmupRequests = observations.Warmup.Count,
      WarmupFailures = observations.Warmup.Count(s => !s.Succeeded),
      Requests = measured.Count,
      Succeeded = succeeded.Count,
      DocsPerSecond = measuredSeconds > 0 ? Math.Round(succeeded.Count / measuredSeconds, 3) : 0,
      DrainSeconds =
        observations.MeasuredEnd > observations.StopIssuing
          ? Math.Round(
            Stopwatch
              .GetElapsedTime(observations.StopIssuing, observations.MeasuredEnd)
              .TotalSeconds,
            2
          )
          : 0,
      Latency = Summarize(succeeded.Select(s => s.LatencyMilliseconds)),
      LatencyAll = Summarize(measured.Select(s => s.LatencyMilliseconds)),
      Errors = measured
        .Where(s => !s.Succeeded)
        .GroupBy(s => s.Outcome)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count()),
      ErrorDetails = measured
        .Concat(observations.Warmup)
        .Where(s => !s.Succeeded && s.Detail is not null)
        .GroupBy(s => s.Outcome)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Detail!),
      PageCounts = succeeded
        .GroupBy(s => s.Pages)
        .OrderBy(g => g.Key)
        .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
      MedianPdfBytes = succeeded.Count > 0 ? Median(succeeded.Select(s => (long)s.Bytes)) : null,
      CpuCoresAverage = CpuCores(observations.CgroupAtStart, observations.CgroupAtEnd),
      CpuSecondsPerDoc =
        succeeded.Count > 0
        && CpuSeconds(observations.CgroupAtStart, observations.CgroupAtEnd) is { } cpuSeconds
          ? Math.Round(cpuSeconds / succeeded.Count, 3)
          : null,
      CpuPercentPeakSampled = phaseStats.Count > 0 ? phaseStats.Max(s => s.CpuPercent) : null,
      MemoryPeakSampledBytes =
        observations.Stats.Count > 0 ? observations.Stats.Max(s => s.MemoryBytes) : null,
      MemoryPeakCgroupBytes = observations.CgroupAtEnd.Counters.MemoryPeakBytes,
      OomKills = observations.CgroupAtEnd.Counters.OomKills,
    };
  }

  public static LatencySummary? Summarize(IEnumerable<double> latencies)
  {
    var sorted = latencies.Order().ToArray();
    if (sorted.Length == 0)
    {
      return null;
    }

    return new LatencySummary(
      sorted.Length,
      Round(sorted[0]),
      Round(sorted.Average()),
      Round(Percentile(sorted, 0.50)),
      Round(Percentile(sorted, 0.90)),
      Round(Percentile(sorted, 0.95)),
      Round(Percentile(sorted, 0.99)),
      Round(sorted[^1])
    );
  }

  /// <summary>
  /// Nearest-rank percentile of an ascending array.
  /// </summary>
  public static double Percentile(double[] sorted, double fraction)
  {
    var rank = (int)Math.Ceiling(fraction * sorted.Length);
    return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
  }

  /// <summary>
  /// Average CPU, in cores, between two cgroup reads. Uses the reads' own timestamps: browsers can
  /// keep working after a client gave up on a request, and that work belongs in the average.
  /// </summary>
  private static double? CpuCores(TimedCounters start, TimedCounters end)
  {
    var seconds = Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalSeconds;
    if (
      start.Counters.CpuUsageMicroseconds is not { } before
      || end.Counters.CpuUsageMicroseconds is not { } after
      || seconds <= 0
    )
    {
      return null;
    }

    return Math.Round((after - before) / 1_000_000d / seconds, 2);
  }

  private static double? CpuSeconds(TimedCounters start, TimedCounters end) =>
    start.Counters.CpuUsageMicroseconds is { } before
    && end.Counters.CpuUsageMicroseconds is { } after
      ? (after - before) / 1_000_000d
      : null;

  private static long Median(IEnumerable<long> values)
  {
    var sorted = values.Order().ToArray();
    return sorted[(sorted.Length - 1) / 2];
  }

  private static double Round(double value) => Math.Round(value, 1);
}
