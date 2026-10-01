namespace Atli.Reports.Engine.Chromium;

/// <summary>
/// Base error type for browser operations
/// </summary>
internal abstract record BrowserError(string Message, Exception? Exception = null);

/// <summary>
/// Error occurred with Chromium browser process or communication
/// </summary>
internal sealed record ChromiumError(string Message, Exception? Exception = null)
  : BrowserError(Message, Exception);

/// <summary>
/// Error occurred with WebSocket connection to browser DevTools
/// </summary>
internal sealed record ConnectionError(string Message, Exception? Exception = null)
  : BrowserError(Message, Exception);

/// <summary>
/// Operation timed out
/// </summary>
internal sealed record TimeoutError(string Operation, TimeSpan Timeout)
  : BrowserError($"Operation '{Operation}' timed out after {Timeout}");

/// <summary>
/// Browser or page pool has been exhausted
/// </summary>
internal sealed record PoolExhaustedError(string ResourceType, int MaxSize)
  : BrowserError($"{ResourceType} pool exhausted (max: {MaxSize})");

/// <summary>
/// Error occurred during content rendering
/// </summary>
internal sealed record RenderError(string Message, Exception? Exception = null)
  : BrowserError(Message, Exception);
