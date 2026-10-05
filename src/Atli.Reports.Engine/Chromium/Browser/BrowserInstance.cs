using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Network;
using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Chromium.Protocol.Messages;
using Atli.Reports.Engine.Chromium.Protocol.Results;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// One running browser process and its browser-level DevTools connection, shared by many
/// conversions. Each conversion gets its own isolated page (see <see cref="CreatePageAsync"/>).
/// </summary>
/// <remarks>
/// The counters (<see cref="ConversionsStarted"/>, <see cref="InFlight"/>) and the
/// <see cref="Retired"/> flag belong to <see cref="BrowserManager"/> and change only under its lock.
/// </remarks>
internal sealed class BrowserInstance : IAsyncDisposable
{
  private readonly BrowserProcess _process;
  private readonly DevToolsConnection _connection;
  private readonly ILogger _logger;
  private readonly TimeSpan _commandTimeout;
  private readonly int _pdfReadChunkSize;
  private readonly ReportsEngineNetworkOptions _network;
  private readonly Lock _warmLock = new();
  private Task<IsolatedPage>? _warmPage;
  private bool _closing;

  private BrowserInstance(
    BrowserProcess process,
    DevToolsConnection connection,
    int generation,
    long startedTimestamp,
    ReportsEngineBrowserOptions options,
    ILogger logger,
    ReportsEngineNetworkOptions network
  )
  {
    _process = process;
    _connection = connection;
    Generation = generation;
    StartedTimestamp = startedTimestamp;
    _commandTimeout = options.CommandTimeout;
    _pdfReadChunkSize = options.PdfReadChunkSize;
    _logger = logger;
    _network = network;
    Terminated = Task.WhenAny(process.Exited, connection.Closed);
  }

  /// <summary>
  /// Counts the browsers the engine has launched; the first is 1.
  /// </summary>
  public int Generation { get; }

  /// <summary>
  /// When the browser started, as a <see cref="TimeProvider"/> timestamp.
  /// </summary>
  public long StartedTimestamp { get; }

  /// <summary>
  /// The browser process id.
  /// </summary>
  public int ProcessId => _process.Id;

  /// <summary>
  /// The browser's temporary profile directory.
  /// </summary>
  public string ProfileDirectory => _process.ProfileDirectory;

  /// <summary>
  /// The browser's DevTools endpoint. Exposed for tests.
  /// </summary>
  internal Uri Endpoint => _process.Endpoint;

  /// <summary>
  /// Completes when the browser process exits or its DevTools connection drops.
  /// </summary>
  public Task Terminated { get; }

  /// <summary>
  /// Whether the browser is still running and reachable.
  /// </summary>
  public bool IsAlive => _connection.IsOpen && !_process.Exited.IsCompleted;

  /// <summary>
  /// The conversions this browser has accepted. Guarded by the manager's lock.
  /// </summary>
  public int ConversionsStarted { get; set; }

  /// <summary>
  /// The conversions currently using this browser. Guarded by the manager's lock.
  /// </summary>
  public int InFlight { get; set; }

  /// <summary>
  /// Whether the browser takes no new conversions and closes once <see cref="InFlight"/> reaches
  /// zero. Guarded by the manager's lock.
  /// </summary>
  public bool Retired { get; set; }

  /// <summary>
  /// Whether closing the browser has started. Guarded by the manager's lock.
  /// </summary>
  public bool CloseStarted { get; set; }

  /// <summary>
  /// Launches a browser and connects to it.
  /// </summary>
  public static async Task<BrowserInstance> LaunchAsync(
    ReportsEngineBrowserOptions options,
    int generation,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    ReportsEngineNetworkOptions network,
    CancellationToken cancellationToken
  )
  {
    var logger = loggerFactory.CreateLogger<BrowserInstance>();
    var process = await BrowserProcess.LaunchAsync(
      options,
      loggerFactory.CreateLogger<BrowserProcess>(),
      cancellationToken,
      network.Mode != ReportsEngineNetworkMode.Unrestricted
    );

    DevToolsConnection connection;
    try
    {
      using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      connectTimeout.CancelAfter(options.StartupTimeout);
      connection = await DevToolsConnection.ConnectAsync(
        process.Endpoint,
        options.CommandTimeout,
        loggerFactory.CreateLogger<DevToolsConnection>(),
        connectTimeout.Token
      );
    }
    catch (Exception exception)
    {
      await process.DisposeAsync();
      if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
      {
        throw;
      }

      throw new BrowserUnavailableException(
        $"Could not connect to the browser's DevTools endpoint: {exception.Message}",
        exception
      );
    }

    BrowserInstance instance;
    try
    {
      instance = new(
        process,
        connection,
        generation,
        timeProvider.GetTimestamp(),
        options,
        logger,
        network
      );
    }
    catch
    {
      await connection.DisposeAsync();
      await process.DisposeAsync();
      throw;
    }
    LogMessages.BrowserLaunched(logger, generation, process.Id);
    return instance;
  }

  /// <summary>
  /// Opens a page in a browser context of its own, so nothing the page stores (cookies, storage,
  /// cache, service workers) is visible to any other conversion. A page prepared in advance is used
  /// when one is ready, and the next one is prepared in the background.
  /// </summary>
  public async Task<IsolatedPage> CreatePageAsync(CancellationToken cancellationToken)
  {
    Task<IsolatedPage>? warmPage;
    Task<IsolatedPage> page;
    lock (_warmLock)
    {
      warmPage = _warmPage;
      page = warmPage ?? CreatePageCoreAsync();
      _warmPage = _closing || !IsAlive ? null : CreatePageCoreAsync();
    }

    try
    {
      return await page.WaitAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      DisposeWhenReady(page);
      throw;
    }
    catch (Exception) when (page == warmPage && IsAlive)
    {
      // Preparing the page ahead of time failed (a command timed out under load, say), but the
      // browser is still there: open one now rather than failing the conversion.
      page = CreatePageCoreAsync();
      try
      {
        return await page.WaitAsync(cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        DisposeWhenReady(page);
        throw;
      }
    }
  }

  /// <summary>
  /// Disposes a browser context and everything in it. Best effort: failures are logged.
  /// </summary>
  public async Task DisposeContextAsync(string browserContextId)
  {
    if (!_connection.IsOpen)
    {
      return;
    }

    DevToolsMessage dispose = new("Target.disposeBrowserContext");
    dispose.Parameters.Add("browserContextId", browserContextId);
    try
    {
      using var reply = await _connection.SendAsync(dispose, null, CancellationToken.None);
    }
    catch (Exception exception)
    {
      LogMessages.BrowserContextDisposeFailed(_logger, exception, browserContextId);
    }
  }

  /// <summary>
  /// Closes the connection, kills the process tree, and deletes the profile directory.
  /// </summary>
  public async ValueTask DisposeAsync()
  {
    Task<IsolatedPage>? warmPage;
    lock (_warmLock)
    {
      _closing = true;
      warmPage = _warmPage;
      _warmPage = null;
    }

    await _connection.DisposeAsync();
    if (warmPage is not null)
    {
      // The connection is closed, so this only releases the page's resources.
      DisposeWhenReady(warmPage);
    }

    await _process.DisposeAsync();
  }

  private async Task<IsolatedPage> CreatePageCoreAsync()
  {
    // The commands run without the caller's token: abandoning them halfway could leave a browser
    // context the engine no longer knows about. They are bounded by the command timeout.
    string? browserContextId = null;
    PageNetworkController? network = null;
    DenyNetworkProxy? proxy = null;
    try
    {
      DevToolsMessage createContext = new("Target.createBrowserContext");
      if (_network.Mode != ReportsEngineNetworkMode.Unrestricted)
      {
        proxy = new DenyNetworkProxy();
        createContext.Parameters.Add("proxyServer", proxy.Address);
        // Chromium otherwise bypasses proxies for loopback and link-local destinations.
        createContext.Parameters.Add("proxyBypassList", "<-loopback>");
      }

      using (var created = await _connection.SendAsync(createContext, null, CancellationToken.None))
      {
        browserContextId = created
          .Deserialize(DevToolsResultsContext.Default.CreateBrowserContextResult)
          .BrowserContextId;
      }

      DevToolsMessage createTarget = new("Target.createTarget");
      createTarget.Parameters.Add("url", "about:blank");
      createTarget.Parameters.Add("browserContextId", browserContextId);
      string targetId;
      using (var target = await _connection.SendAsync(createTarget, null, CancellationToken.None))
      {
        targetId = target.Deserialize(DevToolsResultsContext.Default.CreateTargetResult).TargetId;
      }

      DevToolsMessage attach = new("Target.attachToTarget");
      attach.Parameters.Add("targetId", targetId);
      attach.Parameters.Add("flatten", true);
      string sessionId;
      using (var attached = await _connection.SendAsync(attach, null, CancellationToken.None))
      {
        sessionId = attached
          .Deserialize(DevToolsResultsContext.Default.AttachToTargetResult)
          .SessionId;
      }

      var session = _connection.AttachSession(sessionId, targetId);
      if (proxy is not null)
      {
        network = new PageNetworkController(session, _network, proxy);
        await network.EnableAsync();
      }

      return new IsolatedPage(this, session, browserContextId, _pdfReadChunkSize, _logger, network);
    }
    catch
    {
      try
      {
        if (network is not null)
        {
          await network.DisposeAsync();
        }
        else if (proxy is not null)
        {
          await proxy.DisposeAsync();
        }
      }
      finally
      {
        if (browserContextId is not null)
        {
          await DisposeContextAsync(browserContextId);
        }
      }

      throw;
    }
  }

  private static void DisposeWhenReady(Task<IsolatedPage> page) =>
    _ = page.ContinueWith(
      static async task =>
      {
        if (task.IsCompletedSuccessfully)
        {
          await task.Result.DisposeAsync();
        }
        else
        {
          _ = task.Exception;
        }
      },
      CancellationToken.None,
      TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default
    );
}
