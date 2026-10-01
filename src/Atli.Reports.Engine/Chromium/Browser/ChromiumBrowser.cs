using System.Collections.Concurrent;
using System.Diagnostics;
using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Chromium.Protocol.Messages;
using Atli.Reports.Engine.Chromium.Protocol.Responses;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using OneOf;

namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// Chromium browser instance with page pooling
/// </summary>
internal sealed class ChromiumBrowser : IBrowser
{
  private readonly Process _chromiumProcess;
  private readonly DirectoryInfo _dataDirectory;
  private readonly DevToolsConnection _connection;
  private readonly PagePoolLimits _poolLimits;
  private readonly ILogger _logger;
  private readonly IChromiumPageFactory _pageFactory;

  // Page pooling
  private readonly ConcurrentStack<ChromiumPage> _pagePool = new();
  private int _currentPagePoolSize;
  private readonly SemaphoreSlim _poolLock = new(1, 1);

  internal ChromiumBrowser(
    Process chromiumProcess,
    DirectoryInfo dataDirectory,
    DevToolsConnection connection,
    PagePoolLimits poolLimits,
    ILogger logger,
    IChromiumPageFactory pageFactory
  )
  {
    _chromiumProcess = chromiumProcess;
    _dataDirectory = dataDirectory;
    _connection = connection;
    _poolLimits = poolLimits;
    _logger = logger;
    _pageFactory = pageFactory;
  }

  /// <summary>
  /// Creates or retrieves a page from the pool
  /// </summary>
  public async ValueTask<OneOf<ChromiumPage, BrowserError>> CreatePageAsync(
    CancellationToken ct = default
  )
  {
    await _poolLock.WaitAsync(ct);

    try
    {
      // Try to get from pool
      if (_pagePool.TryPop(out var page))
      {
        return page;
      }

      // Check pool limit
      if (_currentPagePoolSize >= _poolLimits.MaxPagePoolSize)
      {
        return new PoolExhaustedError("Page", _poolLimits.MaxPagePoolSize);
      }

      // Create new page
      try
      {
        var newPage = await CreateNewPage(ct);
        _currentPagePoolSize++;
        return newPage;
      }
      catch (Exception ex)
      {
        LogMessages.BrowserCreatePageFailed(_logger, ex, _chromiumProcess.Id);
        return new ChromiumError("Failed to create browser page", ex);
      }
    }
    finally
    {
      _poolLock.Release();
    }
  }

  /// <summary>
  /// Returns a page to the pool for reuse
  /// </summary>
  public void ReleasePage(ChromiumPage page)
  {
    // Check page health before returning to pool
    if (ShouldDisposePage(page))
    {
      // Dispose the page asynchronously without blocking
      _ = Task.Run(async () =>
      {
        try
        {
          await DisposePage(page);
        }
        catch (Exception ex)
        {
          LogMessages.BrowserPageDisposeFailed(_logger, ex, page.PageId);
        }
      });
      return;
    }

    // Reset page state and return to pool
    page.ResetState();
    _pagePool.Push(page);
  }

  /// <summary>
  /// Determines if a page should be disposed based on health checks
  /// </summary>
  private bool ShouldDisposePage(ChromiumPage page)
  {
    // Dispose if page has errors
    if (page.HasError)
    {
      return true;
    }

    // Dispose if page exceeded max usage count
    if (_poolLimits.MaxPageUsageCount > 0 && page.UsageCount >= _poolLimits.MaxPageUsageCount)
    {
      return true;
    }

    // Dispose if page exceeded max age
    if (_poolLimits.MaxPageAge > TimeSpan.Zero && page.Age >= _poolLimits.MaxPageAge)
    {
      return true;
    }

    return false;
  }

  /// <summary>
  /// Disposes a page and removes it from the pool
  /// </summary>
  public ValueTask DisposePageAsync(ChromiumPage page) => DisposePage(page);

  private async ValueTask<ChromiumPage> CreateNewPage(CancellationToken ct)
  {
    DevToolsMessage createTargetMessage = new("Target.createTarget");
    createTargetMessage.Parameters.Add("url", "about:blank");

    await _connection.ConnectAsync(ct);
    return await _connection.SendAsync(
      createTargetMessage,
      CreateTargetResponseSerializationContext.Default.DevToolsResponseCreateTargetResponse,
      async targetResponse =>
      {
        if (targetResponse.Result == null)
        {
          throw new InvalidOperationException("Target creation response result is null");
        }

        var pageUrl =
          $"{_connection.Uri.Scheme}://{_connection.Uri.Host}:{_connection.Uri.Port}/devtools/page/{targetResponse.Result.TargetId}";
        var page = await _pageFactory.CreatePage(targetResponse.Result.TargetId, new Uri(pageUrl));
        return page;
      },
      ct
    );
  }

  private async ValueTask DisposePage(ChromiumPage page, CancellationToken ct = default)
  {
    await _poolLock.WaitAsync(ct);

    try
    {
      if (_pagePool.Contains(page))
      {
        return;
      }

      await page.DisposeAsync();
      _currentPagePoolSize--;

      DevToolsMessage closeTargetMessage = new("Target.closeTarget");
      closeTargetMessage.Parameters.Add("targetId", page.PageId);
      await _connection.ConnectAsync(ct);

      // Send close target command but don't wait for response
      _connection.SendAsync(closeTargetMessage);
    }
    finally
    {
      _poolLock.Release();
    }
  }

  /// <summary>
  /// Disposes of the browser and all its resources
  /// </summary>
  public async ValueTask DisposeAsync()
  {
    LogMessages.BrowserDispose(_logger, _chromiumProcess.Id);
    _poolLock.Dispose();

    foreach (var page in _pagePool)
    {
      await page.DisposeAsync();
    }

    _chromiumProcess.Kill();
    _chromiumProcess.Dispose();
    await _connection.DisposeAsync();

    if (_dataDirectory.Exists)
    {
      Directory.Delete(_dataDirectory.FullName, true);
    }
  }
}
