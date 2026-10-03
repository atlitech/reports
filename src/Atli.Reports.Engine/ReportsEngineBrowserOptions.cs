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
  /// Chromium's sandbox needs unprivileged user namespaces. In a container, allow them with the seccomp
  /// profile <c>deploy/seccomp/chromium.json</c> from the repository rather than disabling the sandbox;
  /// Ubuntu 23.10 and later also restrict them through AppArmor for processes it does not confine.
  /// A browser that cannot create its sandbox fails to launch, with an error that says what to do; the
  /// engine never disables the sandbox on its own. Only disable it when the HTML is trusted and user
  /// namespaces cannot be allowed.
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
  /// Additional command-line arguments passed to the browser process. Required network restriction
  /// switches take precedence when a restricted network mode is configured.
  /// </summary>
  public IList<string> ExtraArguments { get; } = [];

  /// <summary>
  /// Additional environment variables deliberately passed to Chromium. By default the child process
  /// receives only platform, locale, font, display, path, and temporary-directory settings, not the
  /// application's credentials or configuration. Do not add tokens or secrets here.
  /// </summary>
  public IDictionary<string, string> EnvironmentVariables { get; } =
    new Dictionary<string, string>(StringComparer.Ordinal);

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
  /// The same limit bounds how long the engine waits for a document's <c>load</c> event when
  /// <see cref="PdfOptions.WaitForSignal"/> is not set, and how long printing a long document may take.
  /// </remarks>
  public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Whether to launch the browser when the host starts instead of on the first conversion.
  /// Defaults to <see langword="false"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The engine runs one long-lived browser process for all conversions. By default it starts on
  /// first use, so the first conversion pays the browser's start-up time. Warming up moves that cost
  /// to host start-up. A browser that fails to start does not stop the host: the failure is logged,
  /// the engine's browser health check reports it, and the engine retries the launch in the
  /// background (after 1 second, doubling up to 30 seconds) until one succeeds. The same happens
  /// whenever a launch fails, whether warm-up or a conversion started it.
  /// </para>
  /// <para>
  /// Warm-up runs from a hosted service, so it needs a host (for example a
  /// <c>WebApplication</c> or a generic host).
  /// </para>
  /// </remarks>
  public bool WarmUpOnStartup { get; set; }

  /// <summary>
  /// How many conversions one browser process serves before the engine replaces it with a fresh one.
  /// Defaults to 1000. <c>0</c> never replaces the process for this reason.
  /// </summary>
  /// <remarks>
  /// Replacing the process bounds the memory a long-running browser accumulates. The replacement
  /// starts at once; conversions already running finish on the old process, which closes when the
  /// last of them is done.
  /// </remarks>
  public int MaxConversionsPerProcess { get; set; } = 1000;

  /// <summary>
  /// How long one browser process is used before the engine replaces it with a fresh one. Defaults to
  /// one hour. <see cref="Timeout.InfiniteTimeSpan"/> never replaces the process for this reason.
  /// </summary>
  /// <remarks>
  /// The age is checked when a conversion starts. Replacement drains gracefully, as for
  /// <see cref="MaxConversionsPerProcess"/>.
  /// </remarks>
  public TimeSpan MaxProcessLifetime { get; set; } = TimeSpan.FromHours(1);

  /// <summary>
  /// How long the browser may sit without conversions before the engine closes it. Defaults to
  /// <see cref="Timeout.InfiniteTimeSpan"/>: the browser stays up.
  /// </summary>
  /// <remarks>
  /// A closed browser holds no memory; the next conversion starts a new one and pays its start-up
  /// time. Useful on hosts that bill for memory or scale to zero between bursts of work. Must be
  /// greater than zero, or infinite.
  /// </remarks>
  public TimeSpan IdleTimeout { get; set; } = Timeout.InfiniteTimeSpan;

  /// <summary>
  /// How long shutting down the engine waits for running conversions to finish before it closes the
  /// browser. Defaults to 10 seconds.
  /// </summary>
  /// <remarks>
  /// When the host stops (or the service provider is disposed), the engine stops accepting
  /// conversions, waits up to this long for the running ones, then kills the browser's process tree
  /// and deletes its temporary profile. Conversions still running at that point fail with
  /// <see cref="ConversionErrorKind.BrowserUnavailable"/>. A host's own shutdown timeout also bounds
  /// the wait.
  /// </remarks>
  public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

  /// <summary>
  /// How long the engine waits before it retries a failed browser launch in the background. Each
  /// further failure doubles the wait, up to <see cref="MaxLaunchRetryDelay"/>; a successful launch
  /// resets it. Not bound from configuration; tests shorten it.
  /// </summary>
  internal TimeSpan LaunchRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// The longest wait between background retries of a failed browser launch. Not bound from
  /// configuration; tests shorten it.
  /// </summary>
  internal TimeSpan MaxLaunchRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How many PDF bytes to request from the browser per read. Not bound from configuration; tests
  /// lower it to exercise multi-chunk streaming.
  /// </summary>
  internal int PdfReadChunkSize { get; set; } = Pdf.ChromiumPdfGenerator.DefaultReadChunkSize;
}
