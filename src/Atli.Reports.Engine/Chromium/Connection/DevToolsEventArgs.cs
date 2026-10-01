using System.Text.Json;

namespace Atli.Reports.Engine.Chromium.Connection;

/// <summary>
/// Event data for a CDP event received from the browser
/// </summary>
internal sealed class DevToolsEventArgs(string method, JsonElement parameters) : EventArgs
{
  /// <summary>
  /// The CDP event method name (e.g., "Runtime.bindingCalled")
  /// </summary>
  public string Method { get; } = method;

  /// <summary>
  /// The event parameters (already cloned, safe to hold beyond the callback)
  /// </summary>
  public JsonElement Parameters { get; } = parameters;
}
