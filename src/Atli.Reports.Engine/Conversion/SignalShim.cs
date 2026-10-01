using System.Text.Encodings.Web;

namespace Atli.Reports.Engine.Conversion;

/// <summary>
/// Builds the script that lets a page signal completion by calling <c>window.&lt;name&gt;()</c>.
/// </summary>
/// <remarks>
/// <c>Runtime.addBinding</c> exposes a function that requires exactly one string argument. The shim
/// replaces it with a zero-argument wrapper that forwards a fixed payload, so pages can call the
/// signal without arguments.
/// </remarks>
internal static class SignalShim
{
  /// <summary>
  /// The payload the wrapper passes to the CDP binding.
  /// </summary>
  public const string Payload = "ready";

  /// <summary>
  /// Returns a <c>&lt;script&gt;</c> element that wraps the binding named <paramref name="signalName"/>.
  /// </summary>
  /// <remarks>
  /// The name is emitted as an escaped JavaScript string literal, so quotes, backslashes, and
  /// <c>&lt;/script&gt;</c> in it cannot break out of the script.
  /// </remarks>
  public static string CreateScript(string signalName)
  {
    var name = JavaScriptEncoder.Default.Encode(signalName);
    return $"<script>(function(){{var n=\"{name}\";var o=window[n];"
      + $"if(typeof o==='function'){{window[n]=function(){{o('{Payload}')}}}}}})();</script>";
  }

  /// <summary>
  /// Prepends the shim for <paramref name="signalName"/> to <paramref name="html"/>.
  /// </summary>
  public static string Apply(string html, string signalName) => CreateScript(signalName) + html;
}
