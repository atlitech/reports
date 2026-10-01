namespace Atli.Reports.Engine.Chromium;

/// <summary>
/// No browser can serve the conversion: it could not be found or started, it crashed, its DevTools
/// connection dropped, or the engine is shutting down.
/// </summary>
internal class BrowserUnavailableException : Exception
{
  public BrowserUnavailableException() { }

  public BrowserUnavailableException(string message)
    : base(message) { }

  public BrowserUnavailableException(string message, Exception? innerException)
    : base(message, innerException) { }
}

/// <summary>
/// The DevTools connection to the browser closed, usually because the browser process exited.
/// </summary>
internal sealed class BrowserConnectionClosedException : BrowserUnavailableException
{
  public BrowserConnectionClosedException() { }

  public BrowserConnectionClosedException(string message)
    : base(message) { }

  public BrowserConnectionClosedException(string message, Exception? innerException)
    : base(message, innerException) { }
}
