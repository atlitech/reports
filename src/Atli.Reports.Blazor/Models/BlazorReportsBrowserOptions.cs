using Atli.Reports.Blazor.Services.BrowserServices;

namespace Atli.Reports.Blazor.Models;

/// <summary>
/// Options for BlazorReports Browser
/// </summary>
public class BlazorReportsBrowserOptions
{
  /// <summary>
  /// The browser to use for generating a PDF. Defaults to Chrome
  /// </summary>
  public Browsers Browser { get; set; } = Browsers.Chrome;

  /// <summary>
  /// The path to the browser executable
  /// </summary>
  public FileInfo? BrowserExecutableLocation { get; set; }

  /// <summary>
  /// Configures the browser to run without a sandbox
  /// </summary>
  public bool NoSandbox { get; set; }

  /// <summary>
  /// Configures the browser to use tmp dir instead of /dev/shm for shared memory files.
  /// Useful for container scenarios which generally have /dev/shm size constrained.
  /// </summary>
  public bool DisableDevShmUsage { get; set; }

  /// <summary>
  /// Configures the browser to run in a non-headless mode.
  /// When set to true, the browser will be visible, which can be useful for debugging.
  /// </summary>
  public bool DisableHeadless { get; set; }

  /// <summary>
  /// Sets the maximum pool size for the browser. Defaults to 4
  /// </summary>
  public int MaxBrowserPoolSize { get; set; } = 4;

  /// <summary>
  /// Sets the maximum pool size for the browser pages. Defaults to 10
  /// </summary>
  public int MaxBrowserPagePoolSize { get; set; } = 10;

  /// <summary>
  /// Sets the response timeout for the browser. Defaults to 30 seconds
  /// </summary>
  public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
