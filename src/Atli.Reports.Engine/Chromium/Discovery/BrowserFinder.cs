using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Atli.Reports.Engine.Chromium.Discovery;

/// <summary>
/// Searches for the browser executables cross-platform.
/// </summary>
/// <remarks>
/// Each browser is a row of install locations. The search tries, in order: the Windows registry's
/// App Paths entry; each executable name in the current directory; then each executable name in
/// each of the platform's install directories. The platform checks use
/// <see cref="OperatingSystem"/>, which NativeAOT treats as constants, so a Linux or macOS build
/// carries no registry code.
/// </remarks>
internal static class BrowserFinder
{
  private const string MacDirectory = "/Applications";

  private static readonly string[] LinuxDirectories =
  [
    "/usr/local/sbin",
    "/usr/local/bin",
    "/usr/sbin",
    "/usr/bin",
    "/sbin",
    "/bin",
    "/opt/microsoft/edge",
  ];

  /// <summary>
  /// Google Chrome, or Chromium.
  /// </summary>
  private static readonly BrowserLocations Chrome = new(
    WindowsExecutable: "chrome.exe",
    WindowsDirectory: @"Google\Chrome\Application",
    LinuxExecutables: ["google-chrome", "chrome", "chromium", "chromium-browser"],
    MacExecutables:
    [
      "Google Chrome.app/Contents/MacOS/Google Chrome",
      "Chromium.app/Contents/MacOS/Chromium",
    ]
  );

  /// <summary>
  /// Microsoft Edge.
  /// </summary>
  private static readonly BrowserLocations Edge = new(
    WindowsExecutable: "msedge.exe",
    WindowsDirectory: @"Microsoft\Edge\Application",
    LinuxExecutables: ["microsoft-edge-stable", "microsoft-edge-beta", "microsoft-edge-dev"],
    MacExecutables: ["Microsoft Edge.app/Contents/MacOS/Microsoft Edge"]
  );

  /// <summary>
  /// Tries to find the browser
  /// </summary>
  /// <param name="browserKind">The browser to find</param>
  /// <returns>The path of the browser executable if found, otherwise null.</returns>
  public static string? Find(BrowserKind browserKind)
  {
    return browserKind switch
    {
      BrowserKind.Chrome => Find(Chrome),
      BrowserKind.Edge => Find(Edge),
      _ => null,
    };
  }

  private static string? Find(BrowserLocations browser)
  {
    if (OperatingSystem.IsWindows())
    {
      var pathFromRegistry = GetPathFromRegistry(browser.WindowsExecutable);
      if (!string.IsNullOrWhiteSpace(pathFromRegistry))
      {
        return pathFromRegistry;
      }
    }

    var executables = GetExecutables(browser);
    var currentDirectory = Directory.GetCurrentDirectory();
    foreach (var executable in executables)
    {
      var path = Path.Combine(currentDirectory, executable);
      if (File.Exists(path))
      {
        return path;
      }
    }

    var directories = GetApplicationDirectories(browser);
    foreach (var executable in executables)
    {
      foreach (var directory in directories)
      {
        var path = Path.Combine(directory, executable);
        if (File.Exists(path))
        {
          return path;
        }
      }
    }

    return null;
  }

  private static string[] GetExecutables(BrowserLocations browser)
  {
    if (OperatingSystem.IsWindows())
    {
      return [browser.WindowsExecutable];
    }

    if (OperatingSystem.IsLinux())
    {
      return browser.LinuxExecutables;
    }

    if (OperatingSystem.IsMacOS())
    {
      return browser.MacExecutables;
    }

    return [];
  }

  private static string[] GetApplicationDirectories(BrowserLocations browser)
  {
    if (OperatingSystem.IsWindows())
    {
      return
      [
        Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
          browser.WindowsDirectory
        ),
        Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
          browser.WindowsDirectory
        ),
      ];
    }

    if (OperatingSystem.IsLinux())
    {
      return LinuxDirectories;
    }

    if (OperatingSystem.IsMacOS())
    {
      return [MacDirectory];
    }

    return [];
  }

  [SupportedOSPlatform("windows")]
  private static string? GetPathFromRegistry(string executable)
  {
    var key = Registry
      .GetValue(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\"
          + executable,
        "Path",
        string.Empty
      )
      ?.ToString();

    if (key is null)
    {
      return null;
    }

    var path = Path.Combine(key, executable);
    return File.Exists(path) ? path : null;
  }

  /// <summary>
  /// Where one browser is installed on each platform.
  /// </summary>
  /// <param name="WindowsExecutable">The executable's file name on Windows, also its App Paths key.</param>
  /// <param name="WindowsDirectory">The install directory under Program Files (and Program Files (x86)).</param>
  /// <param name="LinuxExecutables">The executable names on Linux, in order of preference.</param>
  /// <param name="MacExecutables">The executable paths inside the app bundles on macOS, in order of preference.</param>
  private sealed record BrowserLocations(
    string WindowsExecutable,
    string WindowsDirectory,
    string[] LinuxExecutables,
    string[] MacExecutables
  );
}
