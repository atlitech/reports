namespace Atli.Reports.Engine.Chromium.Protocol;

/// <summary>
/// The browser did not answer a DevTools command in time.
/// </summary>
internal sealed class DevToolsTimeoutException : TimeoutException
{
  public DevToolsTimeoutException() { }

  public DevToolsTimeoutException(string message)
    : base(message) { }

  public DevToolsTimeoutException(string message, Exception innerException)
    : base(message, innerException) { }

  public DevToolsTimeoutException(string method, TimeSpan timeout)
    : base($"The browser did not answer {method} within {timeout.TotalSeconds:0.###}s.") { }
}
