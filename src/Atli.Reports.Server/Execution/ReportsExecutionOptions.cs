namespace Atli.Reports.Server.Execution;

internal sealed class ReportsExecutionOptions
{
  internal const string SectionName = "ReportsServer:Execution";

  public string Mode { get; set; } = "InProcess";
  public string Backend { get; set; } = "Docker";
  public bool AllowDevelopmentProcess { get; set; }
  public string ProcessExecutablePath { get; set; } = "";
  public string[] ProcessArguments { get; set; } = [];
  public string DockerExecutablePath { get; set; } = "/usr/bin/docker";
  public string Image { get; set; } = "";
  public string Runtime { get; set; } = "runsc";
  public int MaxConcurrentJobs { get; set; } = 2;
  public long MaxPdfBytes { get; set; } = 50 * 1024 * 1024;
  public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);
  public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(5);
  public long MemoryLimitBytes { get; set; } = 1024 * 1024 * 1024;
  public double CpuLimit { get; set; } = 1;
  public int PidsLimit { get; set; } = 256;

  // Explicit operator settings affect the launcher only. Docker does not forward these into
  // worker containers. No request/header/tenant value participates in launcher configuration.
  public Dictionary<string, string> EnvironmentVariables { get; set; } = [];

  internal void Validate()
  {
    if (Mode is not ("InProcess" or "Worker"))
    {
      throw new InvalidOperationException(
        "ReportsServer:Execution:Mode must be InProcess or Worker."
      );
    }
    if (Mode == "InProcess")
    {
      return;
    }
    if (Backend is not ("Process" or "Docker"))
    {
      throw new InvalidOperationException("Worker Backend must be Process or Docker.");
    }
    if (
      Backend == "Process"
      && (!AllowDevelopmentProcess || !Path.IsPathFullyQualified(ProcessExecutablePath))
    )
    {
      throw new InvalidOperationException(
        "Process workers require AllowDevelopmentProcess=true and an absolute ProcessExecutablePath. They are not a security boundary."
      );
    }
    if (
      Backend == "Docker"
      && (
        !Path.IsPathFullyQualified(DockerExecutablePath)
        || string.IsNullOrWhiteSpace(Image)
        || Image.StartsWith('-')
        || Image.Any(char.IsWhiteSpace)
        || string.IsNullOrWhiteSpace(Runtime)
        || Runtime.Any(character =>
          !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'
        )
      )
    )
    {
      throw new InvalidOperationException(
        "Docker workers require an absolute DockerExecutablePath, an Image, and an explicit Runtime. No runtime fallback is permitted."
      );
    }
    if (
      MaxConcurrentJobs is < 1 or > 256
      || MaxPdfBytes is < 5 or > 1024L * 1024 * 1024
      || Timeout <= TimeSpan.Zero
      || Timeout > TimeSpan.FromHours(1)
      || CleanupTimeout < TimeSpan.FromMilliseconds(100)
      || CleanupTimeout > TimeSpan.FromSeconds(30)
      || MemoryLimitBytes is < 16 * 1024 * 1024 or > 64L * 1024 * 1024 * 1024
      || !double.IsFinite(CpuLimit)
      || CpuLimit is < 0.1 or > 128
      || PidsLimit is < 16 or > 65536
    )
    {
      throw new InvalidOperationException(
        "Worker concurrency, output, time, memory, CPU, or process limits are invalid."
      );
    }
    foreach (var (name, value) in EnvironmentVariables)
    {
      if (
        string.IsNullOrWhiteSpace(name)
        || name.Contains('=')
        || name.Any(char.IsControl)
        || value.Contains('\0')
      )
      {
        throw new InvalidOperationException(
          "Worker launcher environment settings contain an invalid name or value."
        );
      }
    }
  }
}
