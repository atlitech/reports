using System.Text.Encodings.Web;

namespace Atli.Reports.Engine.Conversion;

/// <summary>
/// Builds the script that lets a page signal completion by calling <c>window.&lt;name&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// The engine exposes a DevTools binding under its own name, <see cref="BindingName"/>. Bindings
/// accept exactly one string argument, so the script defines the page-facing function as a
/// zero-argument wrapper that looks the binding up when it is called. Looking it up lazily makes the
/// wrapper independent of whether the browser installs the binding before or after the script runs.
/// </para>
/// <para>
/// The engine registers the script with <c>Page.addScriptToEvaluateOnNewDocument</c> rather than
/// writing it into the HTML: a <c>&lt;script&gt;</c> placed before <c>&lt;!DOCTYPE html&gt;</c> would
/// switch the document to quirks mode.
/// </para>
/// </remarks>
internal static class SignalShim
{
  /// <summary>
  /// The name of the DevTools binding behind every signal.
  /// </summary>
  public const string BindingName = "__atliReportsSignal";

  /// <summary>
  /// The payload the wrapper passes to the binding.
  /// </summary>
  public const string Payload = "ready";

  /// <summary>
  /// Returns the script that defines <c>window.<paramref name="signalName"/>()</c>.
  /// </summary>
  /// <remarks>
  /// The name is emitted as an escaped JavaScript string literal, so quotes, backslashes, and
  /// line breaks in it cannot break out of the script.
  /// </remarks>
  public static string CreateScript(string signalName)
  {
    var name = JavaScriptEncoder.Default.Encode(signalName);
    return $"(function(){{var n=\"{name}\";window[n]=function(){{"
      + $"var b=window[\"{BindingName}\"];if(typeof b==='function'){{b('{Payload}')}}}}}})();";
  }
}
