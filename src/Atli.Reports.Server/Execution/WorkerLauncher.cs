using System.Diagnostics;
using System.Globalization;

namespace Atli.Reports.Server.Execution;

/// <summary>Builds a fixed operator-owned invocation, without shells or inherited credentials.</summary>
internal static class WorkerLauncher
{
  private static readonly string[] InheritedNames =
  [
    "PATH",
    "SystemRoot",
    "WINDIR",
    "LANG",
    "LC_ALL",
    "TZ",
  ];

  internal static ProcessStartInfo Create(
    ReportsExecutionOptions options,
    string directory,
    string containerName
  )
  {
    var info = Base(options, directory);
    if (options.Backend == "Process")
    {
      info.FileName = options.ProcessExecutablePath;
      foreach (var argument in options.ProcessArguments)
      {
        info.ArgumentList.Add(argument);
      }
      return info;
    }

    info.FileName = options.DockerExecutablePath;
    Add(
      info,
      "run",
      "--rm",
      "--interactive",
      "--name",
      containerName,
      "--runtime",
      options.Runtime,
      "--network",
      "none",
      "--read-only",
      "--tmpfs",
      "/tmp:rw,nosuid,nodev,size=512m,mode=1777",
      "--user",
      "1654:1654",
      "--cap-drop",
      "ALL",
      "--security-opt",
      "no-new-privileges:true",
      "--init",
      "--memory",
      options.MemoryLimitBytes.ToString(CultureInfo.InvariantCulture),
      "--memory-swap",
      options.MemoryLimitBytes.ToString(CultureInfo.InvariantCulture),
      "--cpus",
      options.CpuLimit.ToString(CultureInfo.InvariantCulture),
      "--pids-limit",
      options.PidsLimit.ToString(CultureInfo.InvariantCulture),
      options.Image
    );
    return info;
  }

  internal static ProcessStartInfo DockerCommand(
    ReportsExecutionOptions options,
    string directory,
    params string[] arguments
  )
  {
    var info = Base(options, directory);
    info.FileName = options.DockerExecutablePath;
    Add(info, arguments);
    return info;
  }

  private static ProcessStartInfo Base(ReportsExecutionOptions options, string directory)
  {
    ProcessStartInfo info = new()
    {
      UseShellExecute = false,
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
      WorkingDirectory = directory,
    };
    info.Environment.Clear();
    foreach (var name in InheritedNames)
    {
      if (Environment.GetEnvironmentVariable(name) is { } value)
      {
        info.Environment[name] = value;
      }
    }
    info.Environment["TMPDIR"] = directory;
    info.Environment["TMP"] = directory;
    info.Environment["TEMP"] = directory;
    // Docker otherwise falls back to the user's platform home even after HOME is removed.
    // The job directory is newly created and contains no contexts or credential helpers.
    if (options.Backend == "Docker")
      info.Environment["DOCKER_CONFIG"] = directory;
    foreach (var (name, value) in options.EnvironmentVariables)
    {
      info.Environment[name] = value;
    }
    return info;
  }

  private static void Add(ProcessStartInfo info, params string[] arguments)
  {
    foreach (var argument in arguments)
    {
      info.ArgumentList.Add(argument);
    }
  }
}
