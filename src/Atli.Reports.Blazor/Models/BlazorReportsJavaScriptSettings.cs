namespace Atli.Reports.Blazor.Models;

/// <summary>
/// Settings for reports whose JavaScript must finish before the PDF is printed.
/// </summary>
/// <remarks>
/// When <see cref="WaitForCompletedSignal"/> is <see langword="true"/>, the report template defines
/// <c>window.blazorReport.completed()</c>. Call it from the report's JavaScript once its asynchronous work
/// (fetching data, drawing charts, loading fonts) is done; the PDF is printed at that moment. A report that
/// does not call it within <see cref="CompletedSignalTimeout"/> fails with
/// <see cref="Atli.Reports.Engine.ConversionErrorKind.SignalTimeout"/>, which report endpoints answer with
/// 504 Gateway Timeout.
/// </remarks>
public class BlazorReportsJavaScriptSettings
{
  /// <summary>
  /// The default of <see cref="CompletedSignalTimeout"/>: 30 seconds.
  /// </summary>
  public static readonly TimeSpan DefaultCompletedSignalTimeout = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Whether to wait for the report to call <c>blazorReport.completed()</c> before printing the PDF.
  /// Defaults to <see langword="false"/>, which prints as soon as the HTML has loaded.
  /// </summary>
  public bool WaitForCompletedSignal { get; set; }

  /// <summary>
  /// How long to wait for <c>blazorReport.completed()</c>. Defaults to 30 seconds. Use
  /// <see cref="Timeout.InfiniteTimeSpan"/> to wait until the request is canceled.
  /// </summary>
  public TimeSpan CompletedSignalTimeout { get; set; } = DefaultCompletedSignalTimeout;

  internal BlazorReportsJavaScriptSettings Clone() =>
    (BlazorReportsJavaScriptSettings)MemberwiseClone();

  /// <summary>
  /// Throws when the settings cannot be used, so a misconfigured report fails at registration instead of
  /// on every request.
  /// </summary>
  internal void Validate(string paramName)
  {
    if (
      CompletedSignalTimeout < TimeSpan.Zero
      && CompletedSignalTimeout != Timeout.InfiniteTimeSpan
    )
    {
      throw new ArgumentOutOfRangeException(
        paramName,
        CompletedSignalTimeout,
        "The completed signal timeout must not be negative, except Timeout.InfiniteTimeSpan."
      );
    }
  }
}
