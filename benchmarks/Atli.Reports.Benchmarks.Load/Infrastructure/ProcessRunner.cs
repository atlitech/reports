using System.Diagnostics;

namespace Atli.Reports.Benchmarks.Load.Infrastructure;

/// <summary>
/// The outcome of a finished child process.
/// </summary>
internal sealed record ProcessResult(int ExitCode, string Output, string Error)
{
  public bool Succeeded => ExitCode == 0;

  public string Describe() =>
    $"exit code {ExitCode}: {(string.IsNullOrWhiteSpace(Error) ? Output : Error).Trim()}";
}

/// <summary>
/// Runs child processes (docker, git, sysctl) and captures their output.
/// </summary>
internal static class ProcessRunner
{
  public static async Task<ProcessResult> RunAsync(
    string fileName,
    IEnumerable<string> arguments,
    IReadOnlyDictionary<string, string>? environment = null,
    CancellationToken cancellationToken = default
  )
  {
    ProcessStartInfo info = new(fileName)
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    foreach (var argument in arguments)
    {
      info.ArgumentList.Add(argument);
    }

    if (environment is not null)
    {
      foreach (var (key, value) in environment)
      {
        info.Environment[key] = value;
      }
    }

    using var process =
      Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
    var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var error = process.StandardError.ReadToEndAsync(cancellationToken);
    try
    {
      await process.WaitForExitAsync(cancellationToken);
    }
    catch (OperationCanceledException)
    {
      process.Kill(entireProcessTree: true);
      throw;
    }

    return new ProcessResult(process.ExitCode, await output, await error);
  }

  /// <summary>
  /// Runs a command and returns its trimmed output, or <see langword="null"/> when it cannot run or fails.
  /// </summary>
  public static async Task<string?> TryReadAsync(string fileName, params string[] arguments)
  {
    try
    {
      var result = await RunAsync(fileName, arguments);
      return result.Succeeded ? result.Output.Trim() : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
      return null;
    }
  }
}
