using System.IO.Pipelines;
using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Diagnostics;
using Atli.Reports.Engine.Health;
using Atli.Reports.Engine.Pdf;
using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Engine.Conversion;

/// <summary>
/// Converts HTML to PDF in a Chromium browser launched for each conversion.
/// </summary>
internal sealed class HtmlToPdfConverter(
  IBrowserFactory browserFactory,
  ChromiumPdfGenerator pdfGenerator,
  ConversionHealthTracker healthTracker,
  ILogger<HtmlToPdfConverter> logger
) : IHtmlToPdfConverter
{
  public async ValueTask<OneOf<Stream, ConversionError>> ConvertAsync(
    string html,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  )
  {
    ArgumentNullException.ThrowIfNull(html);

    var result = await ConvertCoreAsync(html, options ?? new PdfOptions(), cancellationToken);
    RecordOutcome(result);
    return result;
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

    var result = await ConvertAsync(html, options, cancellationToken);
    if (result.TryPickT1(out var error, out var pdf))
    {
      return error;
    }

    await using (pdf)
    {
      try
      {
        await pdf.CopyToAsync(destination, cancellationToken);
      }
      catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
      {
        return ConversionErrors.Canceled(exception);
      }
    }

    return new Success();
  }

  private void RecordOutcome(OneOf<Stream, ConversionError> result)
  {
    if (result.TryPickT1(out var error, out _))
    {
      // Callers canceling or sending an invalid request say nothing about the engine's health.
      if (error.Kind is not (ConversionErrorKind.Canceled or ConversionErrorKind.InvalidRequest))
      {
        healthTracker.RecordFailure(error.Message);
      }

      return;
    }

    healthTracker.RecordSuccess();
  }

  private async ValueTask<OneOf<Stream, ConversionError>> ConvertCoreAsync(
    string html,
    PdfOptions options,
    CancellationToken cancellationToken
  )
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

    if (cancellationToken.IsCancellationRequested)
    {
      return ConversionErrors.Canceled();
    }

    IBrowser? browser = null;
    ChromiumPage? page = null;
    var success = false;

    try
    {
      // Step 1: Create browser
      OneOf<IBrowser, BrowserError> browserResult;
      try
      {
        browserResult = await browserFactory.CreateBrowserAsync(cancellationToken);
      }
      catch (Exception exception)
      {
        LogMessages.FailedToCreateBrowser(logger, exception.Message);
        return ConversionErrors.FromLaunchException(exception, cancellationToken);
      }

      if (browserResult.TryPickT1(out var browserError, out browser))
      {
        LogMessages.FailedToCreateBrowser(logger, browserError.Message);
        return ConversionErrors.FromBrowserError(
          "Failed to create browser",
          browserError,
          ConversionErrorKind.BrowserUnavailable,
          cancellationToken
        );
      }

      // Step 2: Create page
      var pageResult = await browser.CreatePageAsync(cancellationToken);
      if (pageResult.TryPickT1(out var pageError, out page))
      {
        LogMessages.FailedToCreatePage(logger, pageError.Message);
        return ConversionErrors.FromBrowserError(
          "Failed to create page",
          pageError,
          ConversionErrorKind.BrowserUnavailable,
          cancellationToken
        );
      }

      // Step 3: Register the signal binding BEFORE loading the HTML, so a page that signals
      // immediately cannot race the registration.
      SignalAwaiter? signalAwaiter = null;
      try
      {
        var htmlToSet = html;
        if (signalName is not null)
        {
          LogMessages.SignalWaitRequested(logger, signalName);
          signalAwaiter = await page.RegisterSignalAsync(signalName, cancellationToken);

          // Runtime.addBinding creates a function that requires one string argument; the shim
          // lets the page call window.<signalName>() without arguments.
          htmlToSet = SignalShim.Apply(html, signalName);
        }

        // Step 4: Set HTML content
        var contentResult = await page.SetContentAsync(htmlToSet, cancellationToken);
        if (contentResult.TryPickT1(out var renderError, out _))
        {
          LogMessages.FailedToSetHtmlContent(logger);
          return ConversionErrors.FromBrowserError(
            "Failed to set HTML content",
            renderError,
            ConversionErrorKind.RenderFailed,
            cancellationToken
          );
        }

        // Step 5: Wait for the signal, if configured
        if (signalAwaiter is not null)
        {
          var signalResult = await signalAwaiter.WaitAsync(options.WaitTimeout, cancellationToken);
          if (signalResult.TryPickT1(out var timeoutError, out _))
          {
            LogMessages.SignalTimeoutDuringConversion(logger, signalName!, timeoutError.Timeout);
            return ConversionErrors.SignalTimeout(signalName!, timeoutError.Timeout);
          }
        }

        // Step 6: Generate PDF
        MemoryStream pdfStream = new();
        var pipeWriter = PipeWriter.Create(pdfStream, new StreamPipeWriterOptions(leaveOpen: true));

        var pdfResult = await pdfGenerator.GeneratePdfAsync(
          page,
          pipeWriter,
          options,
          cancellationToken
        );

        if (pdfResult.TryPickT1(out var pdfError, out _))
        {
          LogMessages.PdfGenerationFailedWithError(logger, pdfError.Message);
          await pdfStream.DisposeAsync();
          return pdfError;
        }

        pdfStream.Position = 0;
        success = true;
        return pdfStream;
      }
      finally
      {
        if (signalAwaiter is not null)
        {
          await signalAwaiter.DisposeAsync();
        }
      }
    }
    catch (Exception exception)
    {
      LogMessages.HtmlToPdfConversionFailed(logger, exception);
      return ConversionErrors.FromException(
        "HTML to PDF conversion failed",
        exception,
        ConversionErrorKind.RenderFailed,
        cancellationToken
      );
    }
    finally
    {
      await ReleaseAsync(browser, page, success);
    }
  }

  private async ValueTask ReleaseAsync(IBrowser? browser, ChromiumPage? page, bool success)
  {
    if (browser is null)
    {
      return;
    }

    // Cleanup is best effort: a failure here must not turn a finished conversion into an exception.
    try
    {
      if (page is not null)
      {
        if (success)
        {
          // Return the healthy page to the pool for reuse
          browser.ReleasePage(page);
        }
        else
        {
          // Dispose a page that encountered errors
          await browser.DisposePageAsync(page);
        }
      }

      await browser.DisposeAsync();
    }
    catch (Exception exception)
    {
      LogMessages.BrowserCleanupFailed(logger, exception);
    }
  }
}
