using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Atli.Reports.AppHost.Tests.Support;

/// <summary>
/// Finds the processes a process started, directly or through its children, by reading <c>ps</c>.
/// </summary>
internal static partial class ProcessTree
{
  /// <summary>
  /// The processes descended from <paramref name="processId"/>, with the name of each one's
  /// executable.
  /// </summary>
  [UnsupportedOSPlatform("windows")]
  public static async Task<IReadOnlyList<(int ProcessId, string Name)>> GetDescendantsAsync(
    int processId,
    CancellationToken cancellationToken
  )
  {
    ProcessStartInfo startInfo = new("ps")
    {
      RedirectStandardOutput = true,
      UseShellExecute = false,
    };
    // Every process, as "pid ppid command"; the command is the executable (macOS prints its path).
    foreach (var argument in new[] { "-A", "-o", "pid=,ppid=,comm=" })
    {
      startInfo.ArgumentList.Add(argument);
    }

    using var ps =
      Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ps.");
    var output = await ps.StandardOutput.ReadToEndAsync(cancellationToken);
    await ps.WaitForExitAsync(cancellationToken);
    if (ps.ExitCode != 0)
    {
      throw new InvalidOperationException($"ps exited with code {ps.ExitCode}.");
    }

    Dictionary<int, List<(int ProcessId, string Name)>> children = [];
    var running = false;
    foreach (Match line in PsLine().Matches(output))
    {
      var child = int.Parse(line.Groups["pid"].Value, CultureInfo.InvariantCulture);
      var parent = int.Parse(line.Groups["ppid"].Value, CultureInfo.InvariantCulture);
      var name = Path.GetFileName(line.Groups["command"].Value.Trim());
      if (!children.TryGetValue(parent, out var siblings))
      {
        children[parent] = siblings = [];
      }

      siblings.Add((child, name));
      running |= child == processId;
    }

    // A process that is gone has no descendants either, which would prove nothing.
    if (!running)
    {
      throw new InvalidOperationException($"Process {processId} is not running.");
    }

    List<(int ProcessId, string Name)> descendants = [];
    Queue<int> pending = new([processId]);
    while (pending.TryDequeue(out var parent))
    {
      foreach (var child in children.GetValueOrDefault(parent, []))
      {
        descendants.Add(child);
        pending.Enqueue(child.ProcessId);
      }
    }

    return descendants;
  }

  /// <summary>
  /// Whether a process name is a Chromium-based browser's: Chrome, Chromium, chrome-headless-shell,
  /// or Edge, and their helper processes.
  /// </summary>
  public static bool IsBrowser(string name) => BrowserName().IsMatch(name);

  [GeneratedRegex(@"^\s*(?<pid>\d+)\s+(?<ppid>\d+)\s+(?<command>.+)$", RegexOptions.Multiline)]
  private static partial Regex PsLine();

  [GeneratedRegex("chrom|msedge|microsoft edge", RegexOptions.IgnoreCase)]
  private static partial Regex BrowserName();
}
