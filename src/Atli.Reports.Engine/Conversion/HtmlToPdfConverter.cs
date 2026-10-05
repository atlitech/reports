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
    using var activity = EngineActivities.StartConversion(html, options);
    try
    {
      var error = Validate(html, options);
      if (error is null && cancellationToken.IsCancellationRequested)
      {
        error = ConversionErrors.Canceled();
      }

      if (error is null)
      {
        error = await RunWithDeadlineAsync(html, options, destination, activity, cancellationToken);
        RecordOutcome(error);
      }

      metrics.ConversionFinished(Stopwatch.GetElapsedTime(started), error?.Kind);
      EngineActivities.ConversionFinished(activity, error);
      return error;
    }
    catch (Exception exception)
    {
      // Failures become a ConversionError; only the destination's own exceptions get here.
      EngineActivities.Failed(activity, exception);
      throw;
    }
  }

  private async ValueTask<ConversionError?> RunWithDeadlineAsync(
    string html,
    PdfOptions options,
    Stream destination,
    Activity? activity,
    CancellationToken cancellationToken
  )
  {
    if (_conversionTimeout == Timeout.InfiniteTimeSpan)
    {
      return await RunAsync(html, options, destination, activity, cancellationToken);
    }

    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(_conversionTimeout);
    var error = await RunAsync(html, options, destination, activity, deadline.Token);

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
    Activity? activity,
    CancellationToken cancellationToken
  )
  {
    ConversionLimiter.Permit permit;
    var acquire = limiter.AcquireAsync(cancellationToken);

    // Only a conversion that has to wait for its turn gets a queue span.
    using (
      var queued = acquire.IsCompleted
        ? null
        : EngineActivities.Source.StartActivity(EngineActivities.Spans.QueueWait)
    )
    {
      try
      {
        permit = await acquire;
      }
      catch (Exception exception)
        when (exception is ConversionBusyException or OperationCanceledException)
      {
        var error = ConversionErrors.FromException(OpenStage, exception, cancellationToken);
        EngineActivities.StageFailed(queued, error);
        return error;
      }
    }

    using (permit)
    {
      var stage = OpenStage;
      var span = EngineActivities.Source.StartActivity(EngineActivities.Spans.PageOpen);
      try
      {
        await using var page = await browsers.OpenPageAsync(cancellationToken);

        span = EngineActivities.NextStage(span, EngineActivities.Spans.SetContent);
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
        span = EngineActivities.NextStage(span, EngineActivities.Spans.PageWait);
        span?.SetTag(EngineActivities.Tags.WaitFor, signalName is null ? "load" : "signal");
        if (signalName is not null)
        {
          if (!await page.WaitForSignalAsync(options.WaitTimeout, cancellationToken))
          {
            LogMessages.SignalTimeoutDuringConversion(logger, signalName, options.WaitTimeout);
            var timedOut = ConversionErrors.SignalTimeout(signalName, options.WaitTimeout);
            EngineActivities.StageFailed(span, timedOut);
            return timedOut;
          }
        }
        else
        {
          await page.WaitForLoadAsync(cancellationToken);
        }

        stage = PrintStage;
        span = EngineActivities.NextStage(span, EngineActivities.Spans.Print);
        var size = await page.PrintToPdfAsync(options, destination, cancellationToken);
        span?.SetTag(EngineActivities.Tags.PdfSize, size);
        activity?.SetTag(EngineActivities.Tags.PdfSize, size);

        // The page closes next; that is not printing.
        span?.Dispose();
        span = null;
        return null;
      }
      catch (DestinationWriteException exception) when (!cancellationToken.IsCancellationRequested)
      {
        // The caller's stream failed, not the engine: hand its exception back unchanged.
        EngineActivities.StageFailed(span, exception.InnerException!);
        ExceptionDispatchInfo.Throw(exception.InnerException!);
        throw;
      }
      catch (Exception exception)
      {
        var error = ConversionErrors.FromException(stage, exception, cancellationToken);
        var expected = ConversionErrors.IsExpected(exception);
        EngineActivities.StageFailed(span, error, unexpected: !expected);
        if (expected)
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
      finally
      {
        span?.Dispose();
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
        or ConversionErrorKind.PolicyDenied
        or ConversionErrorKind.Busy
      )
    )
    {
      healthTracker.RecordFailure();
    }
  }
}
