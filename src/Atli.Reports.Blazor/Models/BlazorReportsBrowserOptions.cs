using Atli.Reports.Blazor.Services.BrowserServices;

namespace Atli.Reports.Blazor.Models;

/// <summary>
/// Options for the browser that converts reports to PDF.
/// </summary>
/// <remarks>
/// Atli.Reports.Engine owns the browser. <c>AddBlazorReports</c> copies every option set here to a value
/// other than its default onto <see cref="Atli.Reports.Engine.ReportsEngineOptions.Browser"/>; options left
/// at their defaults do not overwrite engine settings configured with <c>services.AddReportsEngine(...)</c>.
/// </remarks>
public class BlazorReportsBrowserOptions
{
  /// <summary>
  /// The default of <see cref="ResponseTimeout"/>.
  /// </summary>
  internal static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(30);

  /// <summary>
  /// The browser to use for generating a PDF. Defaults to Chrome. Maps onto
  /// <see cref="Atli.Reports.Engine.ReportsEngineBrowserOptions.Kind"/>.
  /// </summary>
  public Browsers Browser { get; set; } = Browsers.Chrome;

  /// <summary>
  /// The path to the browser executable. Maps onto
  /// <see cref="Atli.Reports.Engine.ReportsEngineBrowserOptions.ExecutablePath"/>.
  /// </summary>
  public FileInfo? BrowserExecutableLocation { get; set; }

  /// <summary>
  /// Configures the browser to run without a sandbox. Maps onto
  /// <see cref="Atli.Reports.Engine.ReportsEngineBrowserOptions.NoSandbox"/>.
  /// </summary>
  public bool NoSandbox { get; set; }

  /// <summary>
  /// Configures the browser to use tmp dir instead of /dev/shm for shared memory files.
  /// Useful for container scenarios which generally have /dev/shm size constrained. Maps onto
  /// <see cref="Atli.Reports.Engine.ReportsEngineBrowserOptions.DisableDevShmUsage"/>.
  /// </summary>
  public bool DisableDevShmUsage { get; set; }

  /// <summary>
  /// Configures the browser to run in a non-headless mode.
  /// When set to true, the browser will be visible, which can be useful for debugging. Maps onto
  /// <see cref="Atli.Reports.Engine.ReportsEngineBrowserOptions.Headless"/>.
  /// </summary>
  public bool DisableHeadless { get; set; }

  /// <summary>
  /// Formerly the maximum number of browser processes.
  /// </summary>
  [Obsolete(ObsoleteMessages.BrowserPoolSize)]
  public int MaxBrowserPoolSize { get; set; } = 4;

  /// <summary>
  /// Formerly the maximum number of browser pages.
  /// </summary>
  [Obsolete(ObsoleteMessages.BrowserPoolSize)]
  public int MaxBrowserPagePoolSize { get; set; } = 10;

  /// <summary>
  /// How long to wait for the browser to answer a single DevTools command. Defaults to 30 seconds. Maps
  /// onto <see cref="Atli.Reports.Engine.ReportsEngineBrowserOptions.CommandTimeout"/>.
  /// </summary>
  public TimeSpan ResponseTimeout { get; set; } = DefaultResponseTimeout;
}
