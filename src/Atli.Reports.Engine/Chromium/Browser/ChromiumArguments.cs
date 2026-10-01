namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// Builds the command line of the browser process.
/// </summary>
internal static class ChromiumArguments
{
  /// <summary>
  /// Builds the arguments for a browser that stores its profile in <paramref name="userDataDirectory"/>
  /// and reports a DevTools endpoint on a free port. <see cref="ReportsEngineBrowserOptions.ExtraArguments"/>
  /// come last, so they can override the engine's own switches.
  /// </summary>
  public static List<string> Build(ReportsEngineBrowserOptions options, string userDataDirectory)
  {
    List<string> arguments =
    [
      "--remote-debugging-port=0",
      $"--user-data-dir={userDataDirectory}",
      "--no-first-run",
      "--no-default-browser-check",
      "--disable-gpu",
      "--hide-scrollbars",
      "--mute-audio",
      "--disable-background-networking",
      "--disable-background-timer-throttling",
      "--disable-backgrounding-occluded-windows",
      "--disable-renderer-backgrounding",
      "--disable-breakpad",
      "--disable-crash-reporter",
      "--disable-client-side-phishing-detection",
      "--disable-component-update",
      "--disable-default-apps",
      "--disable-extensions",
      "--disable-hang-monitor",
      "--disable-ipc-flooding-protection",
      "--disable-prompt-on-repost",
      "--disable-sync",
      "--metrics-recording-only",
      "--password-store=basic",
      "--use-mock-keychain",
    ];

    if (options.Headless)
    {
      arguments.Add("--headless");
    }

    if (options.NoSandbox)
    {
      arguments.Add("--no-sandbox");
    }

    if (options.DisableDevShmUsage)
    {
      arguments.Add("--disable-dev-shm-usage");
    }

    arguments.AddRange(options.ExtraArguments);
    return arguments;
  }
}
