using System.Globalization;

namespace Atli.Reports.Benchmarks.Load.Infrastructure;

/// <summary>
/// cgroup v2 counters read from inside a container.
/// </summary>
/// <param name="CpuUsageMicroseconds">Total CPU time the container has used (<c>cpu.stat usage_usec</c>).</param>
/// <param name="MemoryPeakBytes">Peak memory charged to the container, page cache included (<c>memory.peak</c>).</param>
/// <param name="OomKills">Processes the kernel OOM killer has killed in the container (<c>memory.events oom_kill</c>).</param>
internal sealed record CgroupCounters(
  long? CpuUsageMicroseconds,
  long? MemoryPeakBytes,
  int? OomKills
);

/// <summary>
/// The state of a container after a measurement.
/// </summary>
internal sealed record ContainerState(bool Running, bool OomKilled, int ExitCode, int RestartCount);

/// <summary>
/// Starts, inspects, and stops the benchmark targets through <c>benchmarks/load/compose.yaml</c>.
/// </summary>
internal sealed class DockerHost(
  string composeFile,
  string projectName,
  IReadOnlyDictionary<string, string> composeEnvironment
)
{
  /// <summary>
  /// The compose profiles that hold the targets. Every command names them all, so <c>ps</c> and
  /// <c>down</c> see every target; <c>up</c> still starts only the service it names.
  /// </summary>
  private static readonly string[] Profiles = ["--profile", "atli", "--profile", "gotenberg"];

  public string ProjectName => projectName;

  /// <summary>
  /// Recreates <paramref name="service"/> from scratch and returns its container id.
  /// </summary>
  public async Task<string> StartAsync(string service, CancellationToken cancellationToken)
  {
    var up = await ComposeAsync(
      cancellationToken,
      "up",
      "--detach",
      "--force-recreate",
      "--no-deps",
      "--no-build",
      "--pull",
      "never",
      service
    );
    if (!up.Succeeded)
    {
      throw new InvalidOperationException($"docker compose up {service} failed, {up.Describe()}");
    }

    var ps = await ComposeAsync(cancellationToken, "ps", "--all", "--quiet", service);
    var id = ps.Output.Trim();
    return ps.Succeeded && id.Length > 0
      ? id
      : throw new InvalidOperationException($"No container for {service}, {ps.Describe()}");
  }

  /// <summary>
  /// Stops and removes every container of the project.
  /// </summary>
  public Task<ProcessResult> DownAsync(CancellationToken cancellationToken) =>
    ComposeAsync(cancellationToken, "down", "--remove-orphans", "--timeout", "5");

  /// <summary>
  /// Runs <paramref name="command"/> with <c>sh -c</c> inside the container; <see langword="null"/> on failure.
  /// </summary>
  public static async Task<string?> ExecAsync(string container, string command)
  {
    var result = await ProcessRunner.RunAsync("docker", ["exec", container, "sh", "-c", command]);
    return result.Succeeded ? result.Output : null;
  }

  public static async Task<CgroupCounters> ReadCgroupAsync(string container)
  {
    var output = await ExecAsync(
      container,
      "echo cpu $(grep '^usage_usec' /sys/fs/cgroup/cpu.stat 2>/dev/null | cut -d' ' -f2); "
        + "echo peak $(cat /sys/fs/cgroup/memory.peak 2>/dev/null); "
        + "echo oom $(grep '^oom_kill ' /sys/fs/cgroup/memory.events 2>/dev/null | cut -d' ' -f2)"
    );
    if (output is null)
    {
      return new CgroupCounters(null, null, null);
    }

    long? cpu = null;
    long? peak = null;
    int? oom = null;
    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
      var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
      if (parts.Length < 2 || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var value))
      {
        continue;
      }

      switch (parts[0])
      {
        case "cpu":
          cpu = value;
          break;
        case "peak":
          peak = value;
          break;
        case "oom":
          oom = (int)value;
          break;
      }
    }

    return new CgroupCounters(cpu, peak, oom);
  }

  public static async Task<ContainerState?> InspectStateAsync(string container)
  {
    var result = await ProcessRunner.RunAsync(
      "docker",
      [
        "inspect",
        "--format",
        "{{.State.Running}}|{{.State.OOMKilled}}|{{.State.ExitCode}}|{{.RestartCount}}",
        container,
      ]
    );
    if (!result.Succeeded)
    {
      return null;
    }

    var parts = result.Output.Trim().Split('|');
    return parts.Length == 4
      ? new ContainerState(
        bool.Parse(parts[0]),
        bool.Parse(parts[1]),
        int.Parse(parts[2], CultureInfo.InvariantCulture),
        int.Parse(parts[3], CultureInfo.InvariantCulture)
      )
      : null;
  }

  /// <summary>
  /// Returns the container's environment variables and command line (entrypoint and arguments), for
  /// the results' settings record.
  /// </summary>
  public static async Task<(
    IReadOnlyList<string> Environment,
    IReadOnlyList<string> Command
  )> InspectConfigAsync(string container)
  {
    var env = await ProcessRunner.RunAsync(
      "docker",
      ["inspect", "--format", "{{range .Config.Env}}{{println .}}{{end}}", container]
    );
    var command = await ProcessRunner.RunAsync(
      "docker",
      [
        "inspect",
        "--format",
        "{{range .Config.Entrypoint}}{{println .}}{{end}}{{range .Config.Cmd}}{{println .}}{{end}}",
        container,
      ]
    );
    return (Lines(env.Output), Lines(command.Output));

    static string[] Lines(string text) =>
      text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
  }

  public static async Task SaveLogsAsync(string container, string path)
  {
    var logs = await ProcessRunner.RunAsync("docker", ["logs", "--tail", "2000", container]);
    await File.WriteAllTextAsync(path, logs.Output + logs.Error);
  }

  private Task<ProcessResult> ComposeAsync(
    CancellationToken cancellationToken,
    params string[] arguments
  ) =>
    ProcessRunner.RunAsync(
      "docker",
      ["compose", "--file", composeFile, "--project-name", projectName, .. Profiles, .. arguments],
      composeEnvironment,
      cancellationToken
    );
}
