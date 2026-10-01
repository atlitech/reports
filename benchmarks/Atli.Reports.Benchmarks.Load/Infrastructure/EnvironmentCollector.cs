using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Atli.Reports.Benchmarks.Load.Results;

namespace Atli.Reports.Benchmarks.Load.Infrastructure;

/// <summary>
/// Records the machine, Docker, and source facts a reader needs to judge a result.
/// </summary>
internal static class EnvironmentCollector
{
  /// <summary>
  /// The sources whose last commit identifies the code under test.
  /// </summary>
  private static readonly string[] EngineSources =
  [
    "src/Atli.Reports.Engine",
    "src/Atli.Reports.Server",
  ];

  public static async Task<EnvironmentInfo> CollectAsync(
    string repositoryRoot,
    string projectName,
    IReadOnlyDictionary<string, string> images
  )
  {
    var git = await ProcessRunner.TryReadAsync(
      "git",
      "-C",
      repositoryRoot,
      "rev-parse",
      "--short",
      "HEAD"
    );
    var branch = await ProcessRunner.TryReadAsync(
      "git",
      "-C",
      repositoryRoot,
      "rev-parse",
      "--abbrev-ref",
      "HEAD"
    );
    var status = await ProcessRunner.TryReadAsync(
      "git",
      "-C",
      repositoryRoot,
      "status",
      "--porcelain",
      "--untracked-files=no"
    );
    var engine = await ProcessRunner.TryReadAsync(
      "git",
      ["-C", repositoryRoot, "log", "-1", "--format=%h %s", "--", .. EngineSources]
    );

    Dictionary<string, string> imageIds = [];
    foreach (var (target, image) in images)
    {
      imageIds[target] =
        await ProcessRunner.TryReadAsync("docker", "image", "inspect", "--format", "{{.Id}}", image)
        ?? "(missing)";
    }

    return new EnvironmentInfo
    {
      StartedAt = DateTimeOffset.UtcNow,
      GitCommit = git ?? "unknown",
      GitBranch = branch ?? "unknown",
      GitDirty = !string.IsNullOrEmpty(status),
      EngineCommit = engine ?? "unknown",
      HostOs = await DescribeOsAsync(),
      HostCpu = await DescribeCpuAsync(),
      HostLogicalCores = Environment.ProcessorCount,
      HostMemory = await DescribeMemoryAsync(),
      HostLoadStart = await ReadHostLoadAsync(),
      DotnetRuntime = RuntimeInformation.FrameworkDescription,
      DockerServer = await DescribeDockerAsync(),
      DockerCompose =
        await ProcessRunner.TryReadAsync("docker", "compose", "version", "--short") ?? "unknown",
      OtherContainers = await ListOtherContainersAsync(projectName),
      ImageIds = imageIds,
    };
  }

  /// <summary>
  /// The host's 1, 5, and 15 minute load averages.
  /// </summary>
  public static async Task<string> ReadHostLoadAsync()
  {
    if (OperatingSystem.IsMacOS())
    {
      // "{ 3.12 2.80 2.50 }"
      var load = await ProcessRunner.TryReadAsync("sysctl", "-n", "vm.loadavg");
      return load?.Trim('{', '}', ' ') ?? "unknown";
    }

    return File.Exists("/proc/loadavg")
      ? string.Join(' ', (await File.ReadAllTextAsync("/proc/loadavg")).Split(' ').Take(3))
      : "unknown";
  }

  private static async Task<string> DescribeOsAsync()
  {
    if (OperatingSystem.IsMacOS())
    {
      var version = await ProcessRunner.TryReadAsync("sw_vers", "-productVersion");
      return $"macOS {version} ({RuntimeInformation.OSArchitecture})";
    }

    if (File.Exists("/etc/os-release"))
    {
      var pretty = (await File.ReadAllLinesAsync("/etc/os-release")).FirstOrDefault(l =>
        l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)
      );
      if (pretty is not null)
      {
        return $"{pretty["PRETTY_NAME=".Length..].Trim('"')} ({RuntimeInformation.OSArchitecture})";
      }
    }

    return RuntimeInformation.OSDescription;
  }

  private static async Task<string> DescribeCpuAsync()
  {
    if (OperatingSystem.IsMacOS())
    {
      return await ProcessRunner.TryReadAsync("sysctl", "-n", "machdep.cpu.brand_string")
        ?? "unknown";
    }

    if (File.Exists("/proc/cpuinfo"))
    {
      var model = (await File.ReadAllLinesAsync("/proc/cpuinfo")).FirstOrDefault(l =>
        l.StartsWith("model name", StringComparison.Ordinal)
      );
      if (model is not null)
      {
        return model[(model.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
      }
    }

    return RuntimeInformation.ProcessArchitecture.ToString();
  }

  private static async Task<string> DescribeMemoryAsync()
  {
    long? bytes = null;
    if (OperatingSystem.IsMacOS())
    {
      var text = await ProcessRunner.TryReadAsync("sysctl", "-n", "hw.memsize");
      if (long.TryParse(text, CultureInfo.InvariantCulture, out var value))
      {
        bytes = value;
      }
    }
    else if (File.Exists("/proc/meminfo"))
    {
      var line = (await File.ReadAllLinesAsync("/proc/meminfo")).FirstOrDefault(l =>
        l.StartsWith("MemTotal:", StringComparison.Ordinal)
      );
      var kib = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
      if (long.TryParse(kib, CultureInfo.InvariantCulture, out var value))
      {
        bytes = value * 1024;
      }
    }

    return bytes is { } total
      ? string.Create(CultureInfo.InvariantCulture, $"{total / (1024d * 1024 * 1024):0} GiB RAM")
      : "unknown RAM";
  }

  private static async Task<string> DescribeDockerAsync()
  {
    var json = await ProcessRunner.TryReadAsync("docker", "info", "--format", "{{json .}}");
    if (json is null)
    {
      return "unavailable";
    }

    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    string Text(string name) => root.TryGetProperty(name, out var value) ? value.ToString() : "?";
    var memory =
      root.TryGetProperty("MemTotal", out var mem) && mem.TryGetInt64(out var memBytes)
        ? string.Create(CultureInfo.InvariantCulture, $"{memBytes / (1024d * 1024 * 1024):0.0} GiB")
        : "?";
    return $"{Text("ServerVersion")} on {Text("OperatingSystem")} ({Text("Architecture")}, kernel {Text("KernelVersion")}, cgroup v{Text("CgroupVersion")}), {Text("NCPU")} CPUs and {memory} available to containers";
  }

  private static async Task<IReadOnlyList<string>> ListOtherContainersAsync(string projectName)
  {
    var output = await ProcessRunner.TryReadAsync(
      "docker",
      "ps",
      "--format",
      "{{.Names}}|{{.Label \"com.docker.compose.project\"}}"
    );
    return output is null
      ? []
      :
      [
        .. output
          .Split('\n', StringSplitOptions.RemoveEmptyEntries)
          .Select(line => line.Split('|'))
          .Where(parts => parts.Length < 2 || parts[1] != projectName)
          .Select(parts => parts[0])
          .Order(StringComparer.Ordinal),
      ];
  }
}
