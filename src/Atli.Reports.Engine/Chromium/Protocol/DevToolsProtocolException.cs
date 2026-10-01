using Atli.Reports.Engine.Chromium.Protocol.Responses;

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

  public DevToolsProtocolException(string method, DevToolsProtocolError error)
    : base($"{method} failed: {error.Message} ({error.Code})") { }
}
