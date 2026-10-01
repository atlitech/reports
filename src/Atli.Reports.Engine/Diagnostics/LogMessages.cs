using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Diagnostics;

/// <summary>
/// Source-generated log messages for the reports engine. None of them include the HTML being
/// converted.
/// </summary>
internal static partial class LogMessages
{
  // DevTools connection (100s)

  [LoggerMessage(
    EventId = 100,
    Level = LogLevel.Warning,
    Message = "The DevTools connection to {Uri} was lost"
  )]
  public static partial void DevToolsConnectionLost(ILogger logger, Exception exception, Uri uri);

  [LoggerMessage(
    EventId = 101,
    Level = LogLevel.Warning,
    Message = "Ignored a malformed DevTools message from {Uri}"
  )]
  public static partial void MalformedDevToolsMessage(ILogger logger, Exception exception, Uri uri);

  [LoggerMessage(
    EventId = 102,
    Level = LogLevel.Warning,
    Message = "A DevTools event handler failed for '{Method}'"
  )]
  public static partial void DevToolsEventHandlerFailed(
    ILogger logger,
    Exception exception,
    string method
  );

  [LoggerMessage(
    EventId = 103,
    Level = LogLevel.Debug,
    Message = "The DevTools receive loop for {Uri} ended with an error"
  )]
  public static partial void ReceiveLoopFailed(ILogger logger, Exception exception, Uri uri);

  // Browser process (200s)

  [LoggerMessage(
    EventId = 200,
    Level = LogLevel.Debug,
    Message = "Started browser process {ProcessId} with profile {ProfileDirectory}"
  )]
  public static partial void BrowserProcessStarted(
    ILogger logger,
    int processId,
    string profileDirectory
  );

  [LoggerMessage(
    EventId = 201,
    Level = LogLevel.Debug,
    Message = "Browser process {ProcessId}: {Line}"
  )]
  public static partial void BrowserOutput(ILogger logger, int processId, string line);

  [LoggerMessage(
    EventId = 202,
    Level = LogLevel.Warning,
    Message = "Could not kill browser process {ProcessId}"
  )]
  public static partial void BrowserProcessKillFailed(
    ILogger logger,
    Exception exception,
    int processId
  );

  [LoggerMessage(
    EventId = 203,
    Level = LogLevel.Debug,
    Message = "Browser process {ProcessId} exited; deleting profile {ProfileDirectory}"
  )]
  public static partial void BrowserProcessExited(
    ILogger logger,
    int processId,
    string profileDirectory
  );

  [LoggerMessage(
    EventId = 204,
    Level = LogLevel.Warning,
    Message = "Could not delete the browser profile directory {ProfileDirectory}"
  )]
  public static partial void ProfileDirectoryNotDeleted(
    ILogger logger,
    Exception exception,
    string profileDirectory
  );

  // Browser lifecycle (300s)

  [LoggerMessage(
    EventId = 300,
    Level = LogLevel.Information,
    Message = "Browser {Generation} started (process {ProcessId})"
  )]
  public static partial void BrowserLaunched(ILogger logger, int generation, int processId);

  [LoggerMessage(
    EventId = 301,
    Level = LogLevel.Error,
    Message = "Browser {Generation} failed to start (attempt {Attempt})"
  )]
  public static partial void BrowserLaunchFailed(
    ILogger logger,
    Exception exception,
    int generation,
    int attempt
  );

  [LoggerMessage(
    EventId = 302,
    Level = LogLevel.Information,
    Message = "Recycling browser {Generation} because {Reason}"
  )]
  public static partial void BrowserRecycling(ILogger logger, int generation, string reason);

  [LoggerMessage(
    EventId = 303,
    Level = LogLevel.Warning,
    Message = "Browser {Generation} (process {ProcessId}) exited or disconnected unexpectedly; the next conversion starts a new one"
  )]
  public static partial void BrowserTerminated(ILogger logger, int generation, int processId);

  [LoggerMessage(
    EventId = 304,
    Level = LogLevel.Information,
    Message = "Browser {Generation} (process {ProcessId}) closed"
  )]
  public static partial void BrowserClosed(ILogger logger, int generation, int processId);

  [LoggerMessage(
    EventId = 305,
    Level = LogLevel.Warning,
    Message = "Closing browser {Generation} failed"
  )]
  public static partial void BrowserCloseFailed(
    ILogger logger,
    Exception exception,
    int generation
  );

  [LoggerMessage(
    EventId = 306,
    Level = LogLevel.Warning,
    Message = "Shutdown stopped waiting for {InFlight} running conversion(s); closing the browser"
  )]
  public static partial void ShutdownDrainTimedOut(ILogger logger, int inFlight);

  [LoggerMessage(
    EventId = 308,
    Level = LogLevel.Information,
    Message = "Closing browser {Generation}: no conversions for {IdleTimeout}"
  )]
  public static partial void BrowserIdleClosing(
    ILogger logger,
    int generation,
    TimeSpan idleTimeout
  );

  [LoggerMessage(
    EventId = 307,
    Level = LogLevel.Warning,
    Message = "Warming up the browser failed; the engine retries the launch in the background"
  )]
  public static partial void WarmUpFailed(ILogger logger, Exception exception);

  [LoggerMessage(
    EventId = 309,
    Level = LogLevel.Information,
    Message = "Retrying the browser launch in {Delay} (attempt {Attempt})"
  )]
  public static partial void BrowserLaunchRetryScheduled(
    ILogger logger,
    TimeSpan delay,
    int attempt
  );

  [LoggerMessage(
    EventId = 310,
    Level = LogLevel.Information,
    Message = "Browser {Generation} started after {FailedLaunches} failed launch(es); the engine can convert again"
  )]
  public static partial void BrowserLaunchRecovered(
    ILogger logger,
    int generation,
    int failedLaunches
  );

  // Pages (400s)

  [LoggerMessage(
    EventId = 400,
    Level = LogLevel.Debug,
    Message = "Disposing browser context {BrowserContextId} failed"
  )]
  public static partial void BrowserContextDisposeFailed(
    ILogger logger,
    Exception exception,
    string browserContextId
  );

  [LoggerMessage(
    EventId = 401,
    Level = LogLevel.Debug,
    Message = "Waiting for the page to load failed in the page ({Error}); printing it as it is"
  )]
  public static partial void LoadWaitFailed(ILogger logger, string error);

  // Conversions (900s)

  [LoggerMessage(
    EventId = 900,
    Level = LogLevel.Warning,
    Message = "Conversion failed ({Kind}): {Error}"
  )]
  public static partial void ConversionFailed(
    ILogger logger,
    ConversionErrorKind kind,
    string error
  );

  [LoggerMessage(
    EventId = 901,
    Level = LogLevel.Error,
    Message = "Conversion failed unexpectedly at stage '{Stage}'"
  )]
  public static partial void ConversionFailedUnexpectedly(
    ILogger logger,
    Exception exception,
    string stage
  );

  [LoggerMessage(
    EventId = 1000,
    Level = LogLevel.Debug,
    Message = "Signal wait requested for '{SignalName}'"
  )]
  public static partial void SignalWaitRequested(ILogger logger, string signalName);

  [LoggerMessage(
    EventId = 1001,
    Level = LogLevel.Warning,
    Message = "Signal '{SignalName}' timed out after {Timeout} during conversion"
  )]
  public static partial void SignalTimeoutDuringConversion(
    ILogger logger,
    string signalName,
    TimeSpan timeout
  );
}
