using System.Diagnostics;

namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>Minimizes credentials exposed if a browser process is compromised.</summary>
internal static class BrowserEnvironment
{
  // Deliberately omit HOME/USERPROFILE, proxies, cloud identities, dynamic-loader settings, and
  // application configuration. Chromium gets a separate user-data directory via its arguments.
  private static readonly string[] InheritedNames =
  [
    "PATH",
    "SystemRoot",
    "WINDIR",
    "TEMP",
    "TMP",
    "TMPDIR",
    "LANG",
    "LANGUAGE",
    "LC_ALL",
    "LC_CTYPE",
    "LC_MESSAGES",
    "LC_NUMERIC",
    "LC_TIME",
    "TZ",
    "FONTCONFIG_PATH",
    "FONTCONFIG_FILE",
    "DISPLAY",
    "WAYLAND_DISPLAY",
    "XDG_RUNTIME_DIR",
  ];

  public static void Configure(ProcessStartInfo startInfo, ReportsEngineBrowserOptions options)
  {
    var inherited = InheritedNames
      .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
      .Where(variable => variable.Value is not null)
      .ToArray();
    startInfo.Environment.Clear();
    foreach (var variable in inherited)
    {
      startInfo.Environment[variable.Name] = variable.Value;
    }

    foreach (var (name, value) in options.EnvironmentVariables)
    {
      startInfo.Environment[name] = value;
    }
  }
}
