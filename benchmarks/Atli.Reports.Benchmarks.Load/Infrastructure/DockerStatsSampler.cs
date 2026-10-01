using System.Diagnostics;
using System.Globalization;

namespace Atli.Reports.Benchmarks.Load.Infrastructure;

/// <summary>
/// One <c>docker stats</c> reading.
/// </summary>
/// <param name="Timestamp">When the reading returned (<see cref="Stopwatch.GetTimestamp"/>).</param>
/// <param name="CpuPercent">CPU use in percent of one core (200 = two full cores).</param>
/// <param name="MemoryBytes">Memory use as Docker reports it: the cgroup usage minus inactive page cache.</param>
internal readonly record struct StatsSample(long Timestamp, double CpuPercent, long MemoryBytes);

/// <summary>
/// Polls <c>docker stats --no-stream</c> for one container until stopped. Each poll takes about a second,
/// because Docker measures CPU over an interval, so this is a ~1 Hz sampler: short spikes can be missed.
/// </summary>
internal sealed class DockerStatsSampler : IAsyncDisposable
{
  private readonly List<StatsSample> _samples = [];
  private readonly Lock _samplesLock = new();
  private readonly CancellationTokenSource _stop = new();
  private readonly Task _loop;

  public DockerStatsSampler(string container)
  {
    _loop = Task.Run(() => LoopAsync(container, _stop.Token));
  }

  /// <summary>
  /// Stops sampling and returns every reading taken.
  /// </summary>
  public async Task<IReadOnlyList<StatsSample>> StopAsync()
  {
    await _stop.CancelAsync();
    try
    {
      await _loop;
    }
    catch (OperationCanceledException)
    {
      // Expected: the poll in flight is abandoned.
    }

    lock (_samplesLock)
    {
      return [.. _samples];
    }
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync();
    _stop.Dispose();
  }

  private async Task LoopAsync(string container, CancellationToken cancellationToken)
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      var result = await ProcessRunner.RunAsync(
        "docker",
        ["stats", "--no-stream", "--format", "{{.CPUPerc}}|{{.MemUsage}}", container],
        cancellationToken: cancellationToken
      );
      if (result.Succeeded && TryParse(result.Output, out var cpu, out var memory))
      {
        lock (_samplesLock)
        {
          _samples.Add(new StatsSample(Stopwatch.GetTimestamp(), cpu, memory));
        }
      }
      else
      {
        // The container is gone or Docker is busy; avoid spinning.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
      }
    }
  }

  /// <summary>
  /// Parses <c>"123.45%|1.2GiB / 2GiB"</c>.
  /// </summary>
  internal static bool TryParse(string line, out double cpuPercent, out long memoryBytes)
  {
    cpuPercent = 0;
    memoryBytes = 0;
    var parts = line.Trim().Split('|');
    if (parts.Length != 2)
    {
      return false;
    }

    var cpuText = parts[0].Trim().TrimEnd('%');
    var memoryText = parts[1].Split('/')[0].Trim();
    return double.TryParse(
        cpuText,
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out cpuPercent
      ) && TryParseSize(memoryText, out memoryBytes);
  }

  internal static bool TryParseSize(string text, out long bytes)
  {
    bytes = 0;
    (string Suffix, double Factor)[] units =
    [
      ("KiB", 1024d),
      ("MiB", 1024d * 1024),
      ("GiB", 1024d * 1024 * 1024),
      ("TiB", 1024d * 1024 * 1024 * 1024),
      ("kB", 1000d),
      ("MB", 1000d * 1000),
      ("GB", 1000d * 1000 * 1000),
      ("B", 1d),
    ];
    foreach (var (suffix, factor) in units)
    {
      if (
        text.EndsWith(suffix, StringComparison.Ordinal)
        && double.TryParse(
          text[..^suffix.Length],
          NumberStyles.Float,
          CultureInfo.InvariantCulture,
          out var value
        )
      )
      {
        bytes = (long)(value * factor);
        return true;
      }
    }

    return false;
  }
}
