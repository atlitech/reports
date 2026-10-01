namespace Atli.Reports.Engine.Chromium;

/// <summary>
/// The page the conversion was rendering in crashed or was closed by the browser.
/// </summary>
internal sealed class TargetCrashedException : Exception
{
  public TargetCrashedException()
    : base("The page crashed or was closed while it was rendering.") { }

  public TargetCrashedException(string message)
    : base(message) { }

  public TargetCrashedException(string message, Exception innerException)
    : base(message, innerException) { }
}
