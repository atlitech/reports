using System.Globalization;
using Atli.Reports.Engine;

namespace Atli.Reports.Worker;

internal sealed record WorkerSettings(
  string? BrowserPath,
  bool NoSandbox,
  bool DisableDevShmUsage,
  int MaxJobs
)
{
  // Deliberately no generic host, appsettings loading, CLI configuration, or ambient ReportsEngine
  // binding: customer API keys, identity settings, proxies, and network relaxations are not inputs.
  public static WorkerSettings Read(Func<string, string?> read)
  {
    var path = read("ATLI_WORKER_BROWSER_PATH");
    var noSandbox = Boolean(read("ATLI_WORKER_NO_SANDBOX"), defaultValue: false);
    var disableDevShm = Boolean(read("ATLI_WORKER_DISABLE_DEV_SHM_USAGE"), defaultValue: true);
    var jobs = read("ATLI_WORKER_MAX_JOBS");
    var maxJobs = jobs is null
      ? 1
      : int.Parse(jobs, NumberStyles.None, CultureInfo.InvariantCulture);
    if (maxJobs is < 1 or > 100 || (path is not null && !Path.IsPathFullyQualified(path)))
    {
      throw new InvalidOperationException("The worker configuration is invalid.");
    }
    return new WorkerSettings(path, noSandbox, disableDevShm, maxJobs);
  }

  public void Configure(ReportsEngineOptions options)
  {
    options.Network.Mode = ReportsEngineNetworkMode.Disabled;
    options.Concurrency.MaxConcurrentConversions = 1;
    options.Concurrency.MaxQueueLength = 0;
    options.ConversionTimeout = TimeSpan.FromSeconds(90);
    options.Browser.ExecutablePath = BrowserPath;
    options.Browser.NoSandbox = NoSandbox;
    options.Browser.DisableDevShmUsage = DisableDevShmUsage;
    options.Browser.WarmUpOnStartup = false;
    options.Browser.ShutdownTimeout = TimeSpan.FromSeconds(10);
    options.Browser.MaxConversionsPerProcess = 100;
    options.Browser.MaxProcessLifetime = TimeSpan.FromSeconds(300);
  }

  private static bool Boolean(string? value, bool defaultValue) =>
    value is null ? defaultValue : bool.Parse(value);
}
