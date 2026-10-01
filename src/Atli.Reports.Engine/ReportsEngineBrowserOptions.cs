namespace Atli.Reports.Engine;

/// <summary>
/// Configures how the reports engine finds, launches, and talks to the browser.
/// </summary>
public sealed class ReportsEngineBrowserOptions
{
  /// <summary>
  /// The browser to look for when <see cref="ExecutablePath"/> is not set. Defaults to <see cref="BrowserKind.Chrome"/>.
  /// </summary>
  public BrowserKind Kind { get; set; } = BrowserKind.Chrome;

  /// <summary>
  /// The full path of the browser executable. <see langword="null"/> (the default) searches the
  /// standard install locations of <see cref="Kind"/> on Windows, macOS, and Linux.
  /// </summary>
  public string? ExecutablePath { get; set; }

  /// <summary>
  /// Whether to run the browser without a visible window. Defaults to <see langword="true"/>.
  /// </summary>
  public bool Headless { get; set; } = true;

  /// <summary>
  /// Whether to disable the browser's sandbox (<c>--no-sandbox</c>). Defaults to <see langword="false"/>.
  /// </summary>
  /// <remarks>
  /// Containers and hosts that block the user namespaces Chromium's sandbox needs (for example Ubuntu 24.04
  /// with its default AppArmor policy) require this. Only disable the sandbox when the HTML is trusted.
  /// </remarks>
  public bool NoSandbox { get; set; }

  /// <summary>
  /// Whether to write shared memory files to the temporary directory instead of <c>/dev/shm</c>
  /// (<c>--disable-dev-shm-usage</c>). Defaults to <see langword="false"/>.
  /// </summary>
  /// <remarks>
  /// Useful in containers, which usually limit the size of <c>/dev/shm</c>.
  /// </remarks>
  public bool DisableDevShmUsage { get; set; }

  /// <summary>
  /// Additional command-line arguments passed to the browser process, after the engine's own arguments.
  /// </summary>
  public IList<string> ExtraArguments { get; } = [];

  /// <summary>
  /// How long to wait for a launched browser to start and report its DevTools endpoint. Defaults to 30 seconds.
  /// </summary>
  /// <remarks>
  /// A browser that does not start in time fails the conversion with
  /// <see cref="ConversionErrorKind.BrowserUnavailable"/>. Cold starts on small or busy machines (for
  /// example CI runners with two cores) can take several seconds.
  /// </remarks>
  public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long to wait for the browser to answer a single DevTools command. Defaults to 30 seconds.
  /// </summary>
  /// <remarks>
  /// A command that does not complete in time fails the conversion with <see cref="ConversionErrorKind.Timeout"/>.
  /// </remarks>
  public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
