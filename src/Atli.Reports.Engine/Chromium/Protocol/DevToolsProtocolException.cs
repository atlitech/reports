namespace Atli.Reports.Engine.Chromium.Protocol;

/// <summary>
/// The browser answered a DevTools command with an error instead of a result.
/// </summary>
internal sealed class DevToolsProtocolException : Exception
{
  public DevToolsProtocolException() { }

  public DevToolsProtocolException(string message)
    : base(message) { }

  public DevToolsProtocolException(string message, Exception innerException)
    : base(message, innerException) { }

  public DevToolsProtocolException(string method, int code, string message)
    : base($"{method} failed: {message} ({code})")
  {
    Method = method;
    Code = code;
  }

  /// <summary>
  /// The method of the command that failed.
  /// </summary>
  public string? Method { get; }

  /// <summary>
  /// The DevTools error code.
  /// </summary>
  public int Code { get; }
}
