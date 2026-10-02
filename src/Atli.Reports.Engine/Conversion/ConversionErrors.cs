using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Network;
using Atli.Reports.Engine.Chromium.Protocol;

namespace Atli.Reports.Engine.Conversion;

/// <summary>
/// Maps internal failures onto the public <see cref="ConversionError"/>.
/// </summary>
internal static class ConversionErrors
{
  /// <summary>
  /// Maps an exception thrown during <paramref name="stage"/> of a conversion. Cancellation of
  /// <paramref name="cancellationToken"/> wins over everything else, because the browser layer
  /// reports a canceled command as a failure.
  /// </summary>
  /// <remarks>Messages never contain the HTML being converted.</remarks>
  public static ConversionError FromException(
    string stage,
    Exception exception,
    CancellationToken cancellationToken
  )
  {
    if (cancellationToken.IsCancellationRequested)
    {
      return Canceled(exception);
    }

    return exception switch
    {
      NetworkPolicyException => new ConversionError(
        ConversionErrorKind.PolicyDenied,
        exception.Message,
        exception
      ),
      ConversionBusyException => new ConversionError(
        ConversionErrorKind.Busy,
        exception.Message,
        exception
      ),
      BrowserUnavailableException => new ConversionError(
        ConversionErrorKind.BrowserUnavailable,
        $"{stage}: {exception.Message}",
        exception
      ),
      TimeoutException => new ConversionError(
        ConversionErrorKind.Timeout,
        $"{stage}: {exception.Message}",
        exception
      ),
      DevToolsProtocolException or TargetCrashedException => new ConversionError(
        ConversionErrorKind.RenderFailed,
        $"{stage}: {exception.Message}",
        exception
      ),
      _ => new ConversionError(ConversionErrorKind.RenderFailed, stage, exception),
    };
  }

  /// <summary>
  /// Whether <paramref name="exception"/> is a failure the engine anticipates (a busy engine, a
  /// browser that went away, a timeout, a browser that rejected a command, a crashed page, or
  /// cancellation) rather than a defect.
  /// </summary>
  public static bool IsExpected(Exception exception) =>
    exception
      is ConversionBusyException
        or NetworkPolicyException
        or BrowserUnavailableException
        or TimeoutException
        or DevToolsProtocolException
        or TargetCrashedException
        or OperationCanceledException;

  public static ConversionError Canceled(Exception? exception = null) =>
    new(ConversionErrorKind.Canceled, "The conversion was canceled.", exception);

  public static ConversionError InvalidRequest(string message) =>
    new(ConversionErrorKind.InvalidRequest, message);

  public static ConversionError SignalTimeout(string signalName, TimeSpan timeout) =>
    new(
      ConversionErrorKind.SignalTimeout,
      $"Timed out waiting for signal '{signalName}' after {timeout.TotalSeconds}s"
    );
}
