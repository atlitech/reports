using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Diagnostics;
using Atli.Reports.Engine.Health;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Engine.Conversion;

/// <summary>
/// Converts HTML to PDF in the engine's long-lived browser, one isolated page per conversion.
/// </summary>
internal sealed class HtmlToPdfConverter(
  IBrowserProvider browsers,
  ConversionLimiter limiter,
  ConversionHealthTracker healthTracker,
  EngineMetrics metrics,
  IOptions<ReportsEngineOptions> engineOptions,
  ILogger<HtmlToPdfConverter> logger
) : IHtmlToPdfConverter
{
  private readonly TimeSpan _conversionTimeout = engineOptions.Value.ConversionTimeout;

  private const string OpenStage = "Failed to open a browser page";
  private const string SignalStage = "Failed to register the signal";
  private const string ContentStage = "Failed to set HTML content";
  private const string WaitStage = "Failed waiting for the page";
  private const string PrintStage = "PDF generation failed";

  public async ValueTask<OneOf<Stream, ConversionError>> ConvertAsync(
    string html,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  )
  {
    ArgumentNullException.ThrowIfNull(html);

    MemoryStream pdf = new();
    var error = await ConvertCoreAsync(html, options ?? new PdfOptions(), pdf, cancellationToken);
    if (error is not null)
    {
      await pdf.DisposeAsync();
      return error;
    }

    pdf.Position = 0;
    return pdf;
  }

  public async ValueTask<OneOf<Success, ConversionError>> ConvertAsync(
    string html,
    Stream destination,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  )
  {
    ArgumentNullException.ThrowIfNull(html);
    ArgumentNullException.ThrowIfNull(destination);
    if (!destination.CanWrite)
    {
      throw new ArgumentException("The destination stream must be writable.", nameof(destination));
    }

    var error = await ConvertCoreAsync(
      html,
      options ?? new PdfOptions(),
      destination,
      cancellationToken
    );
    return error is null ? new Success() : error;
  }

  private async ValueTask<ConversionError?> ConvertCoreAsync(
    string html,
    PdfOptions options,
    Stream destination,
    CancellationToken cancellationToken
  )
  {
    var started = Stopwatch.GetTimestamp();
    var error = Validate(html, options);
    if (error is null && cancellationToken.IsCancellationRequested)
    {
      error = ConversionErrors.Canceled();
    }

    if (error is null)
    {
      error = await RunWithDeadlineAsync(html, options, destination, cancellationToken);
      RecordOutcome(error);
    }

    metrics.ConversionFinished(Stopwatch.GetElapsedTime(started), error?.Kind);
    return error;
  }

  private async ValueTask<ConversionError?> RunWithDeadlineAsync(
    string html,
    PdfOptions options,
    Stream destination,
    CancellationToken cancellationToken
  )
  {
    if (_conversionTimeout == Timeout.InfiniteTimeSpan)
    {
      return await RunAsync(html, options, destination, cancellationToken);
    }

    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(_conversionTimeout);
    var error = await RunAsync(html, options, destination, deadline.Token);

    // The deadline surfaces as a cancellation; report it as the timeout it is.
    return
      error is { Kind: ConversionErrorKind.Canceled }
      && !cancellationToken.IsCancellationRequested
      && deadline.IsCancellationRequested
      ? new ConversionError(
        ConversionErrorKind.Timeout,
        string.Create(
          CultureInfo.InvariantCulture,
          $"The conversion did not finish within the conversion timeout of {_conversionTimeout.TotalSeconds:0.###}s."
        ),
        error.Exception
      )
      : error;
  }

  private static ConversionError? Validate(string html, PdfOptions options)
  {
    if (string.IsNullOrWhiteSpace(html))
    {
      return ConversionErrors.InvalidRequest("HTML content is required.");
    }

    var signalName = options.WaitForSignal;
    if (signalName is not null && string.IsNullOrWhiteSpace(signalName))
    {
      return ConversionErrors.InvalidRequest("The signal name must not be blank.");
    }

    if (
      signalName is not null
      && options.WaitTimeout < TimeSpan.Zero
      && options.WaitTimeout != Timeout.InfiniteTimeSpan
    )
    {
      return ConversionErrors.InvalidRequest("The signal wait timeout must not be negative.");
    }

    return null;
  }

  private async ValueTask<ConversionError?> RunAsync(
    string html,
    PdfOptions options,
    Stream destination,
    CancellationToken cancellationToken
  )
  {
    ConversionLimiter.Permit permit;
    try
    {
      permit = await limiter.AcquireAsync(cancellationToken);
    }
    catch (Exception exception)
      when (exception is ConversionBusyException or OperationCanceledException)
    {
      return ConversionErrors.FromException(OpenStage, exception, cancellationToken);
    }

    using (permit)
    {
      var stage = OpenStage;
      try
      {
        await using var page = await browsers.OpenPageAsync(cancellationToken);

        var signalName = options.WaitForSignal;
        if (signalName is not null)
        {
          stage = SignalStage;
          LogMessages.SignalWaitRequested(logger, signalName);
          await page.EnableSignalAsync(signalName, cancellationToken);
        }

        stage = ContentStage;
        await page.SetContentAsync(html, cancellationToken);

        stage = WaitStage;
        if (signalName is not null)
        {
          if (!await page.WaitForSignalAsync(options.WaitTimeout, cancellationToken))
          {
            LogMessages.SignalTimeoutDuringConversion(logger, signalName, options.WaitTimeout);
            return ConversionErrors.SignalTimeout(signalName, options.WaitTimeout);
          }
        }
        else
        {
          await page.WaitForLoadAsync(cancellationToken);
        }

        stage = PrintStage;
        await page.PrintToPdfAsync(options, destination, cancellationToken);
        return null;
      }
      catch (DestinationWriteException exception) when (!cancellationToken.IsCancellationRequested)
      {
        // The caller's stream failed, not the engine: hand its exception back unchanged.
        ExceptionDispatchInfo.Throw(exception.InnerException!);
        throw;
      }
      catch (Exception exception)
      {
        var error = ConversionErrors.FromException(stage, exception, cancellationToken);
        if (ConversionErrors.IsExpected(exception))
        {
          if (error.Kind != ConversionErrorKind.Canceled)
          {
            LogMessages.ConversionFailed(logger, error.Kind, error.Message);
          }
        }
        else
        {
          LogMessages.ConversionFailedUnexpectedly(logger, exception, stage);
        }

        return error;
      }
    }
  }

  private void RecordOutcome(ConversionError? error)
  {
    if (error is null)
    {
      healthTracker.RecordSuccess();
      return;
    }

    // Callers canceling or sending an invalid request, and load shedding, say nothing about whether
    // the engine works.
    if (
      error.Kind
      is not (
        ConversionErrorKind.Canceled
        or ConversionErrorKind.InvalidRequest
        or ConversionErrorKind.Busy
      )
    )
    {
      healthTracker.RecordFailure(error.Message);
    }
  }
}
