using System.Runtime.Versioning;

namespace Atli.Reports.Client.Tests.Support;

/// <summary>
/// A browser executable that records that it was started, then exits as a browser that cannot run.
/// </summary>
internal sealed class BrowserLaunchDetector : IDisposable
{
  private readonly string _directory;

  private BrowserLaunchDetector(string directory)
  {
    _directory = directory;
  }

  /// <summary>
  /// The executable to configure as the engine's browser.
  /// </summary>
  public string Path => System.IO.Path.Combine(_directory, "browser.sh");

  /// <summary>
  /// Whether anything started the executable.
  /// </summary>
  public bool Launched => File.Exists(MarkerPath);

  private string MarkerPath => System.IO.Path.Combine(_directory, "launched");

  [UnsupportedOSPlatform("windows")]
  public static BrowserLaunchDetector Create()
  {
    BrowserLaunchDetector detector = new(
      Directory.CreateTempSubdirectory("atli-reports-client-browser-").FullName
    );
    File.WriteAllText(detector.Path, $"#!/bin/sh\ntouch '{detector.MarkerPath}'\nexit 1\n");
    File.SetUnixFileMode(
      detector.Path,
      UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
    );
    return detector;
  }

  public void Dispose() => Directory.Delete(_directory, recursive: true);
}
