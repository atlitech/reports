using System.Diagnostics;
using System.Runtime.InteropServices;
using BlazorReports.Models;
using BlazorReports.Services.BrowserServices.Helpers;
using BlazorReports.Services.BrowserServices.Logs;
using BlazorReports.Services.BrowserServices.Problems;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneOf;

namespace BlazorReports.Services.BrowserServices.Factories;

/// <summary>
/// Factory for creating browser instances
/// </summary>
internal sealed class BrowserFactory(
  IOptions<BlazorReportsOptions> options,
  ILogger<BrowserFactory> browserFactoryLogger,
  ILogger<Browser> browserLogger,
  IConnectionFactory connectionFactory,
  IBrowserPageFactory browserPageFactory
) : IBrowserFactory
{
  /// <summary>
  /// Creates a new browser instance
  /// </summary>
  /// <returns> The browser instance </returns>
  /// <exception cref="FileNotFoundException"> Thrown when the browser executable is not found </exception>
  /// <exception cref="Exception"> Thrown when the browser process could not be started </exception>
  public async ValueTask<OneOf<Browser, BrowserProblem>> CreateBrowser()
  {
    var browserOptions = options.Value.BrowserOptions;

    var browserExecutableLocation = browserOptions.BrowserExecutableLocation is not null
      ? browserOptions.BrowserExecutableLocation.FullName
      : BrowserFinder.Find(browserOptions.Browser);

    if (!File.Exists(browserExecutableLocation))
    {
      throw new FileNotFoundException(
        $"Could not find browser in location '{browserExecutableLocation}'"
      );
    }

    var temporaryPath = Path.GetTempPath();
    var devToolsDirectory = Path.Combine(temporaryPath, Guid.NewGuid().ToString());
    Directory.CreateDirectory(devToolsDirectory);
    DirectoryInfo devToolsActivePortDirectory = new(devToolsDirectory);
    var devToolsActivePortFile = Path.Combine(devToolsDirectory, "DevToolsActivePort");

    if (File.Exists(devToolsActivePortFile))
    {
      File.Delete(devToolsActivePortFile);
    }

    var chromiumProcess = CreateChromiumProcess(
      browserExecutableLocation,
      devToolsDirectory,
      browserOptions
    );
    try
    {
      var started = chromiumProcess.Start();
      if (!started)
      {
        LogMessages.FailedToStartBrowser(browserFactoryLogger);
        return new BrowserProblem();
      }
    }
    catch (Exception exception)
    {
      LogMessages.FailedToStartBrowser(browserFactoryLogger, exception);
      return new BrowserProblem();
    }

    var lines = await ReadDevToolsActiveFile(devToolsActivePortFile, devToolsActivePortDirectory);
    if (lines.Length != 2)
    {
      LogMessages.CouldNotReadDevToolsActivePort(
        browserFactoryLogger,
        new IOException($"The file '{devToolsActivePortFile}' did not contain 2 lines"),
        devToolsActivePortFile
      );
      return new BrowserProblem();
    }

    LogMessages.BrowserDataDirectoryUsed(browserFactoryLogger, devToolsDirectory);

    Uri uri = new($"ws://127.0.0.1:{lines[0]}{lines[1]}");
    var connection = await connectionFactory.CreateConnection(uri, browserOptions.ResponseTimeout);
    return new Browser(
      chromiumProcess,
      devToolsActivePortDirectory,
      connection,
      browserOptions,
      browserLogger,
      browserPageFactory
    );
  }

  /// <summary>
  /// Creates a new Chromium process
  /// </summary>
  /// <param name="chromiumExeFileName"> The path to the Chromium executable </param>
  /// <param name="devToolsDirectory"> The directory to store the DevTools files </param>
  /// <param name="browserOptions"> The browser options </param>
  /// <returns> The Chromium process </returns>
  private Process CreateChromiumProcess(
    string chromiumExeFileName,
    string devToolsDirectory,
    BlazorReportsBrowserOptions browserOptions
  )
  {
    Process chromiumProcess = new();
    List<string> defaultChromiumArgument =
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
      $"--user-data-dir=\"{devToolsDirectory}\"",
    ];

    if (!browserOptions.DisableHeadless)
    {
      defaultChromiumArgument.Add("--headless");
    }

    if (browserOptions.NoSandbox)
    {
      defaultChromiumArgument.Add("--no-sandbox");
    }

    if (browserOptions.DisableDevShmUsage)
    {
      defaultChromiumArgument.Add("--disable-dev-shm-usage");
    }

    var chromiumArguments = string.Join(" ", defaultChromiumArgument);
    LogMessages.StartingChromiumProcess(browserFactoryLogger, chromiumArguments);

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

  /// <summary>
  /// Raised when the Chromium process exits
  /// </summary>
  /// <param name="sender"> The Chromium process </param>
  /// <param name="e"> The event args </param>
  private void ChromiumProcess_Exited(object? sender, EventArgs e)
  {
    // Log errors with details
    if (sender is not Process process)
    {
      return;
    }

    if (process.ExitCode == 0)
    {
      return;
    }

    var exception = Marshal.GetExceptionForHR(process.ExitCode);
    LogMessages.ChromiumProcessCrashed(browserFactoryLogger, exception, process.ExitCode);
  }

  private static async ValueTask<string[]> ReadDevToolsActiveFile(
    string devToolsActivePortFile,
    DirectoryInfo devToolsActivePortDirectory
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

    CancellationTokenSource cts = new(TimeSpan.FromSeconds(10)); // 10 second timeout
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

    // Register the CancellationToken's callback
    var callback = cts.Token.Register(() =>
      tcs.TrySetException(
        new TimeoutException(
          $"A timeout of 10 seconds exceeded, the file '{devToolsActivePortFile}' did not exist"
        )
      )
    );

    try
    {
      // if the file already exists, immediately return the lines
      if (File.Exists(devToolsActivePortFile))
      {
        return await File.ReadAllLinesAsync(devToolsActivePortFile, cts.Token);
      }

      return await tcs.Task; // Wait for the file to be created or the timeout to occur
    }
    finally
    {
      await callback.DisposeAsync(); // Dispose of callback to remove the createdHandler
      watcher.Dispose(); // Dispose of the watcher
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

        await Task.Delay(TimeSpan.FromMilliseconds(100 * retryCount)); // Exponential backoff
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
          await Task.Delay(TimeSpan.FromMilliseconds(100 * retryCount)); // Exponential backoff
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
