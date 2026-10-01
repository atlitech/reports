using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Atli.Reports.Blazor.Services.BrowserServices.Helpers;

/// <summary>
/// This class searches for the Chrome or Chromium executables cross-platform.
/// </summary>
internal static class ChromeFinder
{
  private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
  private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
  private static readonly bool IsMacOs = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

  private const string ChromeExecutableNameWin = "chrome.exe";
  private const string ChromeExecutableNameLinux1 = "google-chrome";
  private const string ChromeExecutableNameLinux2 = "chrome";
  private const string ChromeExecutableNameLinux3 = "chromium";
  private const string ChromeExecutableNameLinux4 = "chromium-browser";
  private const string ChromeExecutableNameMac1 = "Google Chrome.app/Contents/MacOS/Google Chrome";
  private const string ChromeExecutableNameMac2 = "Chromium.app/Contents/MacOS/Chromium";
  private static readonly string[] LinuxDirectoryLocations =
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
  /// Tries to find Chrome
  /// </summary>
  /// <returns>The path of the Chrome executable if found, otherwise null.</returns>
  internal static string? Find()
  {
    var pathFromRegistry = GetPathFromRegistry();

    if (!string.IsNullOrWhiteSpace(pathFromRegistry))
    {
      return pathFromRegistry;
    }

    var exeNames = GetExeNames();
    var pathFromCurrentDirectory = FindChromeInDirectory(Directory.GetCurrentDirectory(), exeNames);

    if (!string.IsNullOrWhiteSpace(pathFromCurrentDirectory))
    {
      return pathFromCurrentDirectory;
    }

    List<string> directories = [];
    GetApplicationDirectories(directories);

    foreach (var exeName in exeNames)
    {
      foreach (var directory in directories)
      {
        var path = Path.Combine(directory, exeName);
        if (File.Exists(path))
        {
          return path;
        }
      }
    }

    return null;
  }

  private static void GetApplicationDirectories(List<string> directories)
  {
    if (IsWindows)
    {
      const string subDirectory = "Google\\Chrome\\Application";
      directories.Add(
        Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
          subDirectory
        )
      );
      directories.Add(
        Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
          subDirectory
        )
      );
    }
    else if (IsLinux)
    {
      directories.AddRange(LinuxDirectoryLocations);
    }
    else if (IsMacOs)
    {
      directories.Add("/Applications");
    }
  }

  private static string? GetPathFromRegistry()
  {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      return null;
    }

    var key = Registry
      .GetValue(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
        "Path",
        string.Empty
      )
      ?.ToString();

    if (key is null)
    {
      return null;
    }

    var path = Path.Combine(key, ChromeExecutableNameWin);
    return File.Exists(path) ? path : null;
  }

  private static List<string> GetExeNames()
  {
    List<string> exeNames = [];

    if (IsWindows)
    {
      exeNames.Add(ChromeExecutableNameWin);
    }
    else if (IsLinux)
    {
      exeNames.AddRange([
        ChromeExecutableNameLinux1,
        ChromeExecutableNameLinux2,
        ChromeExecutableNameLinux3,
        ChromeExecutableNameLinux4,
      ]);
    }
    else if (IsMacOs)
    {
      exeNames.AddRange([ChromeExecutableNameMac1, ChromeExecutableNameMac2]);
    }

    return exeNames;
  }

  private static string? FindChromeInDirectory(string directory, IEnumerable<string> exeNames)
  {
    foreach (var exeName in exeNames)
    {
      var path = Path.Combine(directory, exeName);
      if (File.Exists(path))
      {
        return path;
      }
    }

    return null;
  }
}
