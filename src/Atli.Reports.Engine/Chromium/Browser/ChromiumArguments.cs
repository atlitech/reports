namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// Builds the command line of the browser process.
/// </summary>
internal static class ChromiumArguments
{
  /// <summary>
  /// Builds the arguments for a browser that stores its profile in <paramref name="userDataDirectory"/>
  /// and reports a DevTools endpoint on a free port. <see cref="ReportsEngineBrowserOptions.ExtraArguments"/>
  /// can override ordinary switches; required restricted-network switches are appended last.
  /// </summary>
  public static List<string> Build(
    ReportsEngineBrowserOptions options,
    string userDataDirectory,
    bool restrictNetwork = false
  )
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
    if (restrictNetwork)
    {
      // HTTP(S)/WebSocket traffic uses the context's deny proxy. Prevent common UDP/direct-DNS
      // alternatives too; only an infrastructure boundary can contain a compromised browser.
      arguments.Add("--disable-quic");
      arguments.Add("--dns-prefetch-disable");
      arguments.Add("--host-resolver-rules=MAP * ~NOTFOUND");
      arguments.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
    }

    return arguments;
  }
}
