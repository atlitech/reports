using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Atli.Reports.Blazor.Services.BrowserServices.Helpers;

/// <summary>
/// This class searches for the Microsoft Edge executables cross-platform.
/// </summary>
internal static class EdgeFinder
{
  private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
  private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
  private static readonly bool IsMacOs = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

  private const string EdgeExecutableNameWin = "msedge.exe";
  private const string ChromeExecutableNameLinux1 = "microsoft-edge-stable";
  private const string ChromeExecutableNameLinux2 = "microsoft-edge-beta";
  private const string ChromeExecutableNameLinux3 = "microsoft-edge-dev";
  private const string ChromeExecutableNameMac = "Microsoft Edge.app/Contents/MacOS/Microsoft Edge";

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
      const string subDirectory = "Microsoft\\Edge\\Application";
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
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe",
        "Path",
        string.Empty
      )
      ?.ToString();

    if (key is null)
    {
      return null;
    }

    var path = Path.Combine(key, EdgeExecutableNameWin);
    return File.Exists(path) ? path : null;
  }

  private static List<string> GetExeNames()
  {
    List<string> exeNames = [];

    if (IsWindows)
    {
      exeNames.Add(EdgeExecutableNameWin);
    }
    else if (IsLinux)
    {
      exeNames.AddRange([
        ChromeExecutableNameLinux1,
        ChromeExecutableNameLinux2,
        ChromeExecutableNameLinux3,
      ]);
    }
    else if (IsMacOs)
    {
      exeNames.AddRange([ChromeExecutableNameMac]);
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
