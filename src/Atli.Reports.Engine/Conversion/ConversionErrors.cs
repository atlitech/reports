using Atli.Reports.Engine.Chromium;

namespace Atli.Reports.Engine.Conversion;

/// <summary>
/// Maps internal browser errors and exceptions onto the public <see cref="ConversionError"/>.
/// </summary>
internal static class ConversionErrors
{
  /// <summary>
  /// Maps an error returned by the browser layer. Cancellation of <paramref name="cancellationToken"/>
  /// wins over everything else, because the browser layer reports a canceled command as a failure.
  /// </summary>
  public static ConversionError FromBrowserError(
    string message,
    BrowserError error,
    ConversionErrorKind fallback,
    CancellationToken cancellationToken
  )
  {
    var detail = $"{message}: {error.Message}";

    if (cancellationToken.IsCancellationRequested)
    {
      return Canceled(error.Exception);
    }

    return error switch
    {
      PoolExhaustedError => new ConversionError(ConversionErrorKind.Busy, detail, error.Exception),
      TimeoutError => new ConversionError(ConversionErrorKind.Timeout, detail, error.Exception),
      _ when error.Exception is TimeoutException => new ConversionError(
        ConversionErrorKind.Timeout,
        detail,
        error.Exception
      ),
      _ => new ConversionError(fallback, detail, error.Exception),
    };
  }

  /// <summary>
  /// Maps an exception thrown while converting. Cancellation of <paramref name="cancellationToken"/>
  /// wins, then a DevTools command timeout, then <paramref name="fallback"/>.
  /// </summary>
  public static ConversionError FromException(
    string message,
    Exception exception,
    ConversionErrorKind fallback,
    CancellationToken cancellationToken
  )
  {
    if (cancellationToken.IsCancellationRequested)
    {
      return Canceled(exception);
    }

    var kind = exception is TimeoutException ? ConversionErrorKind.Timeout : fallback;
    return new ConversionError(kind, message, exception);
  }

  /// <summary>
  /// Maps an exception thrown while launching the browser. Anything other than cancellation means the
  /// browser is unavailable, including a browser that does not report its DevTools port in time.
  /// </summary>
  public static ConversionError FromLaunchException(
    Exception exception,
    CancellationToken cancellationToken
  )
  {
    return cancellationToken.IsCancellationRequested
      ? Canceled(exception)
      : new ConversionError(
        ConversionErrorKind.BrowserUnavailable,
        $"Failed to create browser: {exception.Message}",
        exception
      );
  }

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
