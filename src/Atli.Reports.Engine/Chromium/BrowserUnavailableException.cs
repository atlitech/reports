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
/// The browser exited at start-up because Chromium could not create its sandbox: seccomp, AppArmor,
/// or the kernel denies it the unprivileged user namespaces it needs. The message starts with
/// <see cref="Remedy"/>, followed by <see cref="Detail"/>.
/// </summary>
internal sealed class BrowserSandboxUnavailableException : BrowserUnavailableException
{
  /// <summary>
  /// What an operator can do about it. The engine's own text, not the browser's, so health checks
  /// can show it whole.
  /// </summary>
  public const string Remedy =
    "Chromium could not create its sandbox, which needs unprivileged user namespaces. Run the "
    + "container with the seccomp profile deploy/seccomp/chromium.json, and on Ubuntu 23.10+ make "
    + "sure AppArmor allows user namespaces; or, for trusted HTML only, set "
    + "ReportsEngine:Browser:NoSandbox=true. See docs/security.md#chromiums-sandbox.";

  public BrowserSandboxUnavailableException()
    : this(string.Empty) { }

  public BrowserSandboxUnavailableException(string detail)
    : this(detail, null) { }

  public BrowserSandboxUnavailableException(string detail, Exception? innerException)
    : base(string.IsNullOrEmpty(detail) ? Remedy : $"{Remedy} {detail}", innerException)
  {
    Detail = detail;
  }

  /// <summary>
  /// What happened, without <see cref="Remedy"/>: the browser's exit and its last lines of output.
  /// </summary>
  public string Detail { get; }
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
