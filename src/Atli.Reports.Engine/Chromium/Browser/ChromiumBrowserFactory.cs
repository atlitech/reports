using System.Diagnostics;
using System.Runtime.InteropServices;
using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Discovery;
using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneOf;

namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// Factory for creating Chromium browser instances
/// </summary>
internal sealed class ChromiumBrowserFactory(
  IOptions<ReportsEngineOptions> optionsAccessor,
  ILogger<ChromiumBrowserFactory> factoryLogger,
  ILogger<ChromiumBrowser> browserLogger,
  IDevToolsConnectionFactory connectionFactory,
  IChromiumPageFactory pageFactory
) : IBrowserFactory
{
  /// <summary>
  /// Creates a new browser instance using configured options
  /// </summary>
  public async ValueTask<OneOf<IBrowser, BrowserError>> CreateBrowserAsync(
    CancellationToken ct = default
  )
  {
    var browserOptions = optionsAccessor.Value.Browser;

    // Use configured executable path or auto-detect
    var browserExecutablePath =
      browserOptions.ExecutablePath ?? BrowserFinder.Find(browserOptions.Kind);

    if (!File.Exists(browserExecutablePath))
    {
      return new ChromiumError($"Could not find browser at '{browserExecutablePath}'");
    }

    // Create temporary directory for browser data
    var temporaryPath = Path.GetTempPath();
    var devToolsDirectory = Path.Combine(temporaryPath, Guid.NewGuid().ToString());
    Directory.CreateDirectory(devToolsDirectory);
    DirectoryInfo devToolsActivePortDirectory = new(devToolsDirectory);
    var devToolsActivePortFile = Path.Combine(devToolsDirectory, "DevToolsActivePort");

    if (File.Exists(devToolsActivePortFile))
    {
      File.Delete(devToolsActivePortFile);
    }

    // Start Chromium process
    var chromiumProcess = CreateChromiumProcess(
      browserExecutablePath,
      devToolsDirectory,
      browserOptions
    );

    try
    {
      var started = chromiumProcess.Start();
      if (!started)
      {
        InvalidOperationException error = new("Process.Start() returned false");
        LogMessages.FailedToStartBrowser(factoryLogger, error);
        return new ChromiumError("Failed to start Chromium browser");
      }
    }
    catch (Exception exception)
    {
      LogMessages.FailedToStartBrowser(factoryLogger, exception);
      return new ChromiumError("Failed to start Chromium browser", exception);
    }

    // Read DevTools active port
    var lines = await ReadDevToolsActiveFile(
      devToolsActivePortFile,
      devToolsActivePortDirectory,
      browserOptions.StartupTimeout,
      ct
    );
    if (lines.Length != 2)
    {
      IOException error = new($"The file '{devToolsActivePortFile}' did not contain 2 lines");
      LogMessages.CouldNotReadDevToolsActivePort(factoryLogger, error, devToolsActivePortFile);
      return new ChromiumError("Could not read DevTools active port file");
    }

    LogMessages.BrowserDataDirectoryUsed(factoryLogger, devToolsDirectory);

    // Create connection
    Uri uri = new($"ws://127.0.0.1:{lines[0]}{lines[1]}");
    var connection = await connectionFactory.CreateConnection(uri, browserOptions.CommandTimeout);

    return new ChromiumBrowser(
      chromiumProcess,
      devToolsActivePortDirectory,
      connection,
      PagePoolLimits.Default,
      browserLogger,
      pageFactory
    );
  }

  /// <summary>
  /// Builds the command-line arguments for a Chromium process that stores its profile in
  /// <paramref name="userDataDirectory"/>.
  /// </summary>
  internal static List<string> BuildArguments(
    ReportsEngineBrowserOptions options,
    string userDataDirectory
  )
  {
    List<string> arguments =
    [
      "--disable-gpu",
      "--hide-scrollbars",
      "--mute-audio",
      "--disable-background-networking",
      "--disable-background-timer-throttling",
      "--disable-default-apps",
      "--disable-extensions",
      "--disable-hang-monitor",
      "--disable-prompt-on-repost",
      "--disable-sync",
      "--disable-translate",
      "--metrics-recording-only",
      "--no-first-run",
      "--disable-crash-reporter",
      "--remote-debugging-port=\"0\"",
      $"--user-data-dir=\"{userDataDirectory}\"",
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
    return arguments;
  }

  private Process CreateChromiumProcess(
    string chromiumExeFileName,
    string devToolsDirectory,
    ReportsEngineBrowserOptions options
  )
  {
    Process chromiumProcess = new();
    var chromiumArguments = string.Join(" ", BuildArguments(options, devToolsDirectory));
    LogMessages.StartingChromiumProcess(factoryLogger, chromiumArguments);

    ProcessStartInfo processStartInfo = new()
    {
      FileName = chromiumExeFileName,
      Arguments = chromiumArguments,
      CreateNoWindow = true,
    };

    chromiumProcess.StartInfo = processStartInfo;
    chromiumProcess.Exited += ChromiumProcess_Exited;
    return chromiumProcess;
  }

  private void ChromiumProcess_Exited(object? sender, EventArgs e)
  {
    if (sender is not Process process)
    {
      return;
    }

    if (process.ExitCode == 0)
    {
      return;
    }

    var exception = Marshal.GetExceptionForHR(process.ExitCode);
    LogMessages.ChromiumProcessCrashed(factoryLogger, exception, process.ExitCode);
  }

  /// <summary>
  /// Waits until the browser writes its DevTools port file, for at most <paramref name="startupTimeout"/>.
  /// Throws <see cref="TimeoutException"/> when the time runs out and
  /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is canceled.
  /// </summary>
  private static async ValueTask<string[]> ReadDevToolsActiveFile(
    string devToolsActivePortFile,
    DirectoryInfo devToolsActivePortDirectory,
    TimeSpan startupTimeout,
    CancellationToken ct
  )
  {
    if (devToolsActivePortDirectory is null || !devToolsActivePortDirectory.Exists)
    {
      throw new DirectoryNotFoundException($"The {nameof(devToolsActivePortDirectory)} is null");
    }

    FileSystemWatcher watcher = new()
    {
      Path = devToolsActivePortDirectory.FullName,
      Filter = Path.GetFileName(devToolsActivePortFile),
      EnableRaisingEvents = true,
    };

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(startupTimeout);
    TaskCompletionSource<string[]> tcs = new();

    void CreatedHandler(object s, FileSystemEventArgs e)
    {
      if (e.ChangeType != WatcherChangeTypes.Created)
      {
        return;
      }

      HandleFileCreationAsync(devToolsActivePortFile, tcs, 5, 2).ConfigureAwait(false);
    }

    watcher.Created += CreatedHandler;

    var callback = cts.Token.Register(() =>
    {
      if (ct.IsCancellationRequested)
      {
        tcs.TrySetCanceled(ct);
        return;
      }

      tcs.TrySetException(
        new TimeoutException(
          $"The browser did not start within {startupTimeout.TotalSeconds}s: the file '{devToolsActivePortFile}' did not exist"
        )
      );
    });

    try
    {
      if (File.Exists(devToolsActivePortFile))
      {
        return await File.ReadAllLinesAsync(devToolsActivePortFile, cts.Token);
      }

      return await tcs.Task;
    }
    finally
    {
      await callback.DisposeAsync();
      watcher.Dispose();
    }
  }

  private static async Task HandleFileCreationAsync(
    string filePath,
    TaskCompletionSource<string[]> tcs,
    int maxRetries,
    int expectedLines
  )
  {
    var retryCount = 0;
    while (true)
    {
      try
      {
        if (File.Exists(filePath))
        {
          var lines = await File.ReadAllLinesAsync(filePath);
          if (lines.Length >= expectedLines)
          {
            tcs.TrySetResult(lines);
            break;
          }
        }

        if (++retryCount == maxRetries)
        {
          tcs.TrySetException(
            new IOException(
              $"Unable to read file '{filePath}' with {expectedLines} lines after {maxRetries} attempts"
            )
          );
          break;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100 * retryCount));
      }
      catch (IOException)
      {
        if (++retryCount == maxRetries)
        {
          tcs.TrySetException(
            new IOException($"Unable to read file '{filePath}' after {maxRetries} attempts")
          );
        }
        else
        {
          await Task.Delay(TimeSpan.FromMilliseconds(100 * retryCount));
        }
      }
      catch (Exception ex)
      {
        tcs.TrySetException(ex);
        break;
      }
    }
  }
}
