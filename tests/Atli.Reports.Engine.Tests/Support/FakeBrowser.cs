using System.Runtime.Versioning;
using Atli.Reports.Engine.Chromium.Discovery;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// A shell script standing in for the browser. It records the arguments of every launch, so a test
/// can count launches and find the profile directory the engine gave it, and its body can be
/// replaced while the engine uses it, so a browser that cannot start can be repaired.
/// </summary>
internal sealed class FakeBrowser : IDisposable
{
  private const string ProfileArgumentPrefix = "--user-data-dir=";

  private readonly string _directory;

  private FakeBrowser(string directory)
  {
    _directory = directory;
  }

  public string Path => System.IO.Path.Combine(_directory, "fake-browser.sh");

  /// <summary>
  /// A directory of the fake's own, for files its body creates.
  /// </summary>
  public string Directory => _directory;

  private string ArgumentsFile => System.IO.Path.Combine(_directory, "arguments.txt");

  /// <summary>
  /// Creates the script. It records its arguments, then runs <paramref name="body"/>.
  /// </summary>
  [UnsupportedOSPlatform("windows")]
  public static FakeBrowser Create(string body)
  {
    FakeBrowser fake = new(System.IO.Directory.CreateTempSubdirectory("fake-browser-").FullName);
    fake.Write(fake.Path, body);
    return fake;
  }

  /// <summary>
  /// A body that runs the Chrome (or Chromium) installed on the machine with the engine's arguments.
  /// </summary>
  public static string RealChrome()
  {
    var chrome =
      BrowserFinder.Find(BrowserKind.Chrome)
      ?? throw new InvalidOperationException("No Chrome or Chromium is installed.");
    return $"exec '{chrome}' \"$@\"";
  }

  /// <summary>
  /// Replaces the script's body. Launches already running keep the old one; the next launch runs
  /// the new one.
  /// </summary>
  [UnsupportedOSPlatform("windows")]
  public void Replace(string body)
  {
    var replacement = Path + ".new";
    Write(replacement, body);
    File.Move(replacement, Path, overwrite: true);
  }

  /// <summary>
  /// How many times the engine started the script.
  /// </summary>
  public int CountLaunches()
  {
    try
    {
      return File.ReadLines(ArgumentsFile)
        .Count(line => line.StartsWith(ProfileArgumentPrefix, StringComparison.Ordinal));
    }
    catch (FileNotFoundException)
    {
      return 0;
    }
  }

  public async Task<string> ReadProfileDirectoryAsync()
  {
    var lines = await File.ReadAllLinesAsync(ArgumentsFile);
    return lines.Single(line => line.StartsWith(ProfileArgumentPrefix, StringComparison.Ordinal))[
      ProfileArgumentPrefix.Length..
    ];
  }

  public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);

  [UnsupportedOSPlatform("windows")]
  private void Write(string path, string body)
  {
    File.WriteAllText(
      path,
      $"#!/bin/sh\nfor a in \"$@\"; do echo \"$a\" >> '{ArgumentsFile}'; done\n{body}\n"
    );
    File.SetUnixFileMode(
      path,
      UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
    );
  }
}
