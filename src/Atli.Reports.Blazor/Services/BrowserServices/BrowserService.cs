using System.Collections.Concurrent;
using System.IO.Pipelines;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services.BrowserServices.Factories;
using Atli.Reports.Blazor.Services.BrowserServices.Logs;
using Atli.Reports.Blazor.Services.BrowserServices.Problems;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Services.BrowserServices;

/// <summary>
/// Service for interacting with the browser
/// </summary>
internal sealed class BrowserService : IAsyncDisposable, IBrowserService
{
  private readonly ILogger<BrowserService> _logger;
  private readonly IBrowserFactory _browserFactory;
  private readonly BlazorReportsBrowserOptions _browserOptions;
  private readonly ConcurrentQueue<Browser> _browserQueue = new();
  private readonly SemaphoreSlim _browserPoolLock;
  private readonly SemaphoreSlim _browserStartLock = new(1, 1);
  private int _currentBrowserPoolSize;

  /// <summary>
  /// The connection to the browser
  /// </summary>
  /// <param name="logger"> The logger </param>
  /// <param name="options"> The options for Blazor Reports</param>
  /// <param name="browserFactory"> The factory for creating browser instances </param>
  public BrowserService(
    ILogger<BrowserService> logger,
    IOptions<BlazorReportsOptions> options,
    IBrowserFactory browserFactory
  )
  {
    _logger = logger;
    _browserFactory = browserFactory;
    _browserOptions = options.Value.BrowserOptions;
    _browserPoolLock = new SemaphoreSlim(0, _browserOptions.MaxBrowserPoolSize);
  }

  /// <summary>
  /// Generates a report using the specified HTML
  /// </summary>
  /// <param name="pipeWriter"> The pipe writer to write the report to </param>
  /// <param name="html"> The HTML to use in the report </param>
  /// <param name="pageSettings"> The page settings to use in the report </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <returns> The result of the operation </returns>
  public async ValueTask<
    OneOf<Success, ServerBusyProblem, OperationCancelledProblem, BrowserProblem>
  > GenerateReport(
    PipeWriter pipeWriter,
    string html,
    BlazorReportsPageSettings pageSettings,
    CancellationToken cancellationToken
  )
  {
    var browserGetResult = await GetBrowser();
    var hasBrowserPoolLimitReachedProblem = browserGetResult.TryPickT1(
      out _,
      out var browserOrBrowserProblem
    );
    if (hasBrowserPoolLimitReachedProblem)
    {
      LogMessages.BrowserPoolLimitReachedAndRetryTimeExpired(_logger);
      return new ServerBusyProblem();
    }

    var hasBrowserStartProblem = browserOrBrowserProblem.TryPickT1(
      out var browserStartProblem,
      out var browser
    );
    if (hasBrowserStartProblem)
    {
      LogMessages.FailedToStartBrowser(_logger);
      return browserStartProblem;
    }

    return await browser.GenerateReport(pipeWriter, html, pageSettings, cancellationToken);
  }

  /// <summary>
  /// Gets a browser instance
  /// </summary>
  /// <returns> The browser instance </returns>
  private async ValueTask<OneOf<Browser, PoolLimitReachedProblem, BrowserProblem>> GetBrowser()
  {
    if (_currentBrowserPoolSize < _browserOptions.MaxBrowserPoolSize)
    {
      await _browserStartLock.WaitAsync();
      try
      {
        // Check again in case another thread has already created a browser
        if (_currentBrowserPoolSize < _browserOptions.MaxBrowserPoolSize)
        {
          LogMessages.CreatingNewBrowserInstance(_logger);
          var createBrowserResult = await _browserFactory.CreateBrowser();
          var hasBrowserProblem = createBrowserResult.TryPickT1(
            out var browserProblem,
            out var browser
          );
          if (hasBrowserProblem)
          {
            LogMessages.FailedToCreateBrowser(_logger);
            return browserProblem;
          }
          _browserQueue.Enqueue(browser);
          _currentBrowserPoolSize++;
          _browserPoolLock.Release();
          return browser;
        }
      }
      catch (Exception ex)
      {
        LogMessages.FailedToCreateBrowser(_logger, ex);
        return new BrowserProblem();
      }
      finally
      {
        _browserStartLock.Release();
      }
    }

    await _browserPoolLock.WaitAsync();
    try
    {
      var retryCount = 0;
      while (retryCount < 3)
      {
        _browserQueue.TryDequeue(out var browser);
        if (browser is not null)
        {
          _browserQueue.Enqueue(browser);
          return browser;
        }

        retryCount++;
        await Task.Delay(TimeSpan.FromSeconds(5));
      }

      return new PoolLimitReachedProblem();
    }
    finally
    {
      _browserPoolLock.Release();
    }
  }

  /// <summary>
  /// Disposes of the browser service
  /// </summary>
  public async ValueTask DisposeAsync()
  {
    LogMessages.DisposingOfBrowserService(_logger);
    _browserPoolLock.Dispose();
    _browserStartLock.Dispose();
    while (_browserQueue.TryDequeue(out var browser))
    {
      await browser.DisposeAsync();
    }
  }
}
