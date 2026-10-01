using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Diagnostics;

/// <summary>
/// Source-generated log messages for the reports engine
/// </summary>
internal static partial class LogMessages
{
  // Connection Messages

  [LoggerMessage(
    EventId = 100,
    Level = LogLevel.Error,
    Message = "Unable to establish WebSocket connection to {Uri}"
  )]
  public static partial void UnableToEstablishWebSocketConnection(ILogger logger, Uri uri);

  [LoggerMessage(
    EventId = 101,
    Level = LogLevel.Debug,
    Message = "Send queue processing cancelled for connection {Uri}"
  )]
  public static partial void SendQueueProcessingCancelled(ILogger logger, Uri uri);

  [LoggerMessage(
    EventId = 102,
    Level = LogLevel.Debug,
    Message = "Receive queue processing cancelled for connection {Uri}"
  )]
  public static partial void ReceiveQueueProcessingCancelled(ILogger logger, Uri uri);

  [LoggerMessage(
    EventId = 103,
    Level = LogLevel.Error,
    Message = "Receive queue processing error for connection {Uri}"
  )]
  public static partial void ReceiveQueueProcessingError(ILogger logger, Exception ex, Uri uri);

  // Browser Messages

  [LoggerMessage(EventId = 201, Level = LogLevel.Error, Message = "Failed to start Chrome browser")]
  public static partial void FailedToStartBrowser(ILogger logger, Exception error);

  [LoggerMessage(
    EventId = 202,
    Level = LogLevel.Debug,
    Message = "Disposing of Chrome browser with process id: {BrowserProcessId}"
  )]
  public static partial void BrowserDispose(ILogger logger, int browserProcessId);

  // Browser Pool Messages

  // Page Messages

  [LoggerMessage(
    EventId = 400,
    Level = LogLevel.Error,
    Message = "Failed to create browser page for browser with process id {BrowserProcessId}"
  )]
  public static partial void BrowserCreatePageFailed(
    ILogger logger,
    Exception error,
    int browserProcessId
  );

  [LoggerMessage(
    EventId = 402,
    Level = LogLevel.Debug,
    Message = "Disposing browser page with target ID: {TargetId}"
  )]
  public static partial void BrowserPageDispose(ILogger logger, string targetId);

  [LoggerMessage(
    EventId = 403,
    Level = LogLevel.Error,
    Message = "Failed to dispose browser page with target ID: {TargetId}"
  )]
  public static partial void BrowserPageDisposeFailed(
    ILogger logger,
    Exception exception,
    string targetId
  );

  // BrowserFactory Messages

  [LoggerMessage(
    EventId = 500,
    Level = LogLevel.Debug,
    Message = "Starting Chromium process with arguments: {Arguments}"
  )]
  public static partial void StartingChromiumProcess(ILogger logger, string arguments);

  [LoggerMessage(
    EventId = 501,
    Level = LogLevel.Debug,
    Message = "Browser data directory used: {DataDirectory}"
  )]
  public static partial void BrowserDataDirectoryUsed(ILogger logger, string dataDirectory);

  [LoggerMessage(
    EventId = 502,
    Level = LogLevel.Error,
    Message = "Could not read DevTools active port file: {FilePath}"
  )]
  public static partial void CouldNotReadDevToolsActivePort(
    ILogger logger,
    Exception error,
    string filePath
  );

  [LoggerMessage(
    EventId = 503,
    Level = LogLevel.Error,
    Message = "Chromium process crashed with exit code: {ExitCode}"
  )]
  public static partial void ChromiumProcessCrashed(
    ILogger logger,
    Exception? exception,
    int exitCode
  );

  [LoggerMessage(
    EventId = 505,
    Level = LogLevel.Error,
    Message = "Browser page '{TargetId}' failed to set content"
  )]
  public static partial void BrowserPageSetContentFailed(
    ILogger logger,
    Exception exception,
    string targetId
  );

  // CDP Event Messages

  [LoggerMessage(
    EventId = 600,
    Level = LogLevel.Warning,
    Message = "CDP event handler threw an exception for event '{Method}'"
  )]
  public static partial void CdpEventHandlerError(
    ILogger logger,
    Exception exception,
    string method
  );

  // Signal Messages

  [LoggerMessage(
    EventId = 700,
    Level = LogLevel.Debug,
    Message = "Signal binding '{BindingName}' registered for page '{TargetId}'"
  )]
  public static partial void SignalBindingRegistered(
    ILogger logger,
    string targetId,
    string bindingName
  );

  [LoggerMessage(
    EventId = 701,
    Level = LogLevel.Debug,
    Message = "Waiting for signal '{BindingName}' with timeout {Timeout}"
  )]
  public static partial void WaitingForSignal(ILogger logger, string bindingName, TimeSpan timeout);

  [LoggerMessage(
    EventId = 702,
    Level = LogLevel.Debug,
    Message = "Signal '{BindingName}' received"
  )]
  public static partial void SignalReceived(ILogger logger, string bindingName);

  [LoggerMessage(
    EventId = 703,
    Level = LogLevel.Warning,
    Message = "Signal '{BindingName}' timed out after {Timeout}"
  )]
  public static partial void SignalTimedOut(ILogger logger, string bindingName, TimeSpan timeout);

  // Conversion Messages

  // PDF Generation Messages

  [LoggerMessage(
    EventId = 800,
    Level = LogLevel.Error,
    Message = "PDF generation failed for page {PageId}"
  )]
  public static partial void PdfGenerationFailed(
    ILogger logger,
    Exception exception,
    string pageId
  );

  // HTML to PDF Conversion Messages

  [LoggerMessage(
    EventId = 900,
    Level = LogLevel.Error,
    Message = "Failed to create browser: {Error}"
  )]
  public static partial void FailedToCreateBrowser(ILogger logger, string error);

  [LoggerMessage(EventId = 901, Level = LogLevel.Error, Message = "Failed to create page: {Error}")]
  public static partial void FailedToCreatePage(ILogger logger, string error);

  [LoggerMessage(EventId = 902, Level = LogLevel.Error, Message = "Failed to set HTML content")]
  public static partial void FailedToSetHtmlContent(ILogger logger);

  [LoggerMessage(EventId = 903, Level = LogLevel.Error, Message = "PDF generation failed: {Error}")]
  public static partial void PdfGenerationFailedWithError(ILogger logger, string error);

  [LoggerMessage(EventId = 904, Level = LogLevel.Error, Message = "HTML to PDF conversion failed")]
  public static partial void HtmlToPdfConversionFailed(ILogger logger, Exception exception);

  // Signal Messages

  [LoggerMessage(
    EventId = 1000,
    Level = LogLevel.Information,
    Message = "Signal wait requested for binding '{BindingName}'"
  )]
  public static partial void SignalWaitRequested(ILogger logger, string bindingName);

  [LoggerMessage(
    EventId = 1001,
    Level = LogLevel.Warning,
    Message = "Signal '{BindingName}' timed out after {Timeout} during conversion"
  )]
  public static partial void SignalTimeoutDuringConversion(
    ILogger logger,
    string bindingName,
    TimeSpan timeout
  );

  [LoggerMessage(
    EventId = 905,
    Level = LogLevel.Warning,
    Message = "Failed to clean up the browser after a conversion"
  )]
  public static partial void BrowserCleanupFailed(ILogger logger, Exception exception);
}
