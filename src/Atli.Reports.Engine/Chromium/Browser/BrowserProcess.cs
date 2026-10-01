using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Atli.Reports.Engine.Chromium.Discovery;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// A browser process the engine launched, with its temporary profile directory.
/// </summary>
/// <remarks>
/// <para>
/// The process reports its DevTools endpoint on standard error (<c>DevTools listening on ws://…</c>),
/// which the launcher reads instead of polling for the <c>DevToolsActivePort</c> file. Standard
/// output and error are drained for the life of the process so a chatty browser never blocks on a
/// full pipe; the lines are logged at debug level.
/// </para>
/// <para>
/// Disposing kills the whole process tree, waits for the browser to exit, and only then deletes the
/// profile directory, retrying while the file system still holds it. A launch that fails after the
/// process started cleans up the same way, so a failed start never leaves a browser running.
/// </para>
/// </remarks>
internal sealed class BrowserProcess : IAsyncDisposable
{
  /// <summary>
  /// The prefix of every profile directory the engine creates in the temporary directory.
  /// </summary>
  public const string ProfileDirectoryPrefix = "atli-reports-";

  private const string EndpointMarker = "DevTools listening on ";
  private const int OutputTailLines = 10;
  private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

  private readonly Process _process;
  private readonly ILogger _logger;
  private readonly TaskCompletionSource<Uri> _endpoint = new(
    TaskCreationOptions.RunContinuationsAsynchronously
  );
  private readonly TaskCompletionSource<int> _exited = new(
    TaskCreationOptions.RunContinuationsAsynchronously
  );
  private readonly Queue<string> _outputTail = new();
  private readonly Lock _outputTailLock = new();
  private readonly Lock _gate = new();
  private Task _errorPump = Task.CompletedTask;
  private Task _outputPump = Task.CompletedTask;
  private Task? _disposal;
  private bool _started;

  private BrowserProcess(Process process, string profileDirectory, ILogger logger)
  {
    _process = process;
    ProfileDirectory = profileDirectory;
    _logger = logger;
  }

  /// <summary>
  /// The process id.
  /// </summary>
  public int Id { get; private set; }

  /// <summary>
  /// The temporary profile directory (<c>--user-data-dir</c>).
  /// </summary>
  public string ProfileDirectory { get; }

  /// <summary>
  /// The browser's DevTools WebSocket endpoint.
  /// </summary>
  public Uri Endpoint { get; private set; } = null!;

  /// <summary>
  /// Completes with the exit code when the process exits.
  /// </summary>
  public Task<int> Exited => _exited.Task;

  /// <summary>
  /// Finds and launches the browser, and waits until it reports its DevTools endpoint.
  /// </summary>
  /// <exception cref="BrowserUnavailableException">The browser could not be found or started in time.</exception>
  public static async Task<BrowserProcess> LaunchAsync(
    ReportsEngineBrowserOptions options,
    ILogger logger,
    CancellationToken cancellationToken
  )
  {
    var executable = string.IsNullOrEmpty(options.ExecutablePath)
      ? BrowserFinder.Find(options.Kind)
      : options.ExecutablePath;

    if (string.IsNullOrEmpty(executable))
    {
      throw new BrowserUnavailableException(
        $"No {options.Kind} executable was found. Install it or set ReportsEngine:Browser:ExecutablePath."
      );
    }

    if (!File.Exists(executable))
    {
      throw new BrowserUnavailableException(
        $"The browser executable '{executable}' does not exist."
      );
    }

    // Unique and created atomically; on Unix only the current user can open it (0700).
    var profileDirectory = Directory.CreateTempSubdirectory(ProfileDirectoryPrefix).FullName;

    ProcessStartInfo startInfo = new(executable)
    {
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardError = true,
      RedirectStandardOutput = true,
      StandardErrorEncoding = Encoding.UTF8,
      StandardOutputEncoding = Encoding.UTF8,
    };
    foreach (var argument in ChromiumArguments.Build(options, profileDirectory))
    {
      startInfo.ArgumentList.Add(argument);
    }

    BrowserProcess browser = new(
      new Process { StartInfo = startInfo, EnableRaisingEvents = true },
      profileDirectory,
      logger
    );

    try
    {
      await browser.StartAsync(options.StartupTimeout, cancellationToken);
      return browser;
    }
    catch
    {
      await browser.DisposeAsync();
      throw;
    }
  }

  /// <summary>
  /// Kills the process tree, waits for the browser to exit, then deletes the profile directory.
  /// Safe to call more than once and concurrently; every caller waits for the same cleanup.
  /// </summary>
  public ValueTask DisposeAsync()
  {
    lock (_gate)
    {
      _disposal ??= DisposeCoreAsync();
      return new ValueTask(_disposal);
    }
  }

  internal static bool TryParseEndpoint(string line, out Uri endpoint)
  {
    var index = line.IndexOf(EndpointMarker, StringComparison.Ordinal);
    if (
      index >= 0
      && Uri.TryCreate(
        line[(index + EndpointMarker.Length)..].Trim(),
        UriKind.Absolute,
        out var parsed
      )
      && parsed.Scheme is "ws" or "wss"
    )
    {
      endpoint = parsed;
      return true;
    }

    endpoint = null!;
    return false;
  }

  private async Task StartAsync(TimeSpan startupTimeout, CancellationToken cancellationToken)
  {
    _process.Exited += OnExited;
    try
    {
      if (!_process.Start())
      {
        throw new BrowserUnavailableException("The browser process did not start.");
      }
    }
    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
    {
      throw new BrowserUnavailableException(
        $"The browser process could not be started: {exception.Message}",
        exception
      );
    }

    _started = true;
    Id = _process.Id;
    LogMessages.BrowserProcessStarted(_logger, Id, ProfileDirectory);

    _errorPump = PumpAsync(_process.StandardError, isError: true);
    _outputPump = PumpAsync(_process.StandardOutput, isError: false);

    try
    {
      Endpoint = await _endpoint.Task.WaitAsync(startupTimeout, cancellationToken);
    }
    catch (TimeoutException exception)
    {
      throw new BrowserUnavailableException(
        string.Create(
          CultureInfo.InvariantCulture,
          $"The browser did not report its DevTools endpoint within {startupTimeout.TotalSeconds:0.###}s.{DescribeOutput()}"
        ),
        exception
      );
    }
  }

  private void OnExited(object? sender, EventArgs e)
  {
    int exitCode;
    try
    {
      exitCode = _process.ExitCode;
    }
    catch (InvalidOperationException)
    {
      exitCode = -1;
    }

    _exited.TrySetResult(exitCode);
    if (!_endpoint.Task.IsCompleted)
    {
      _ = FailStartupAfterExitAsync(exitCode);
    }
  }

  private async Task FailStartupAfterExitAsync(int exitCode)
  {
    // Give the error pump a moment to read the browser's last words, which explain the exit.
    try
    {
      await _errorPump.WaitAsync(TimeSpan.FromMilliseconds(500));
    }
    catch (TimeoutException) { }

    _endpoint.TrySetException(
      new BrowserUnavailableException(
        string.Create(
          CultureInfo.InvariantCulture,
          $"The browser exited with code {exitCode} before it reported its DevTools endpoint.{DescribeOutput()}"
        )
      )
    );
  }

  private async Task PumpAsync(StreamReader reader, bool isError)
  {
    try
    {
      while (await reader.ReadLineAsync() is { } line)
      {
        if (isError && !_endpoint.Task.IsCompleted && TryParseEndpoint(line, out var endpoint))
        {
          _endpoint.TrySetResult(endpoint);
          continue;
        }

        lock (_outputTailLock)
        {
          if (_outputTail.Count == OutputTailLines)
          {
            _outputTail.Dequeue();
          }

          _outputTail.Enqueue(line);
        }

        LogMessages.BrowserOutput(_logger, Id, line);
      }
    }
    catch (Exception exception)
      when (exception is IOException or ObjectDisposedException or InvalidOperationException)
    {
      // The process is gone or being disposed.
    }
  }

  private string DescribeOutput()
  {
    lock (_outputTailLock)
    {
      return _outputTail.Count == 0
        ? string.Empty
        : " Browser output: " + string.Join(" | ", _outputTail);
    }
  }

  private async Task DisposeCoreAsync()
  {
    _endpoint.TrySetException(new BrowserUnavailableException("The browser was shut down."));
    _ = _endpoint.Task.Exception;

    if (_started)
    {
      try
      {
        // The whole tree: renderer, GPU, and utility processes as well as the browser itself.
        _process.Kill(entireProcessTree: true);
      }
      catch (InvalidOperationException)
      {
        // Already exited.
      }
      catch (Win32Exception exception)
      {
        LogMessages.BrowserProcessKillFailed(_logger, exception, Id);
      }

      try
      {
        await _process.WaitForExitAsync().WaitAsync(ExitTimeout);
      }
      catch (TimeoutException exception)
      {
        LogMessages.BrowserProcessKillFailed(_logger, exception, Id);
      }

      try
      {
        await Task.WhenAll(_errorPump, _outputPump).WaitAsync(OutputDrainTimeout);
      }
      catch (TimeoutException)
      {
        // A descendant still holds the pipes; disposing the process closes our ends.
      }

      LogMessages.BrowserProcessExited(_logger, Id, ProfileDirectory);
    }

    _process.Exited -= OnExited;
    _process.Dispose();
    await DeleteProfileDirectoryAsync();
  }

  private async Task DeleteProfileDirectoryAsync()
  {
    const int maxAttempts = 10;
    for (var attempt = 1; ; attempt++)
    {
      try
      {
        if (Directory.Exists(ProfileDirectory))
        {
          Directory.Delete(ProfileDirectory, recursive: true);
        }

        return;
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        if (attempt == maxAttempts)
        {
          LogMessages.ProfileDirectoryNotDeleted(_logger, exception, ProfileDirectory);
          return;
        }

        // Files can stay locked for a moment after the processes that held them are gone.
        await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
      }
    }
  }
}
