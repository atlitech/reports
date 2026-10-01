using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// A tiny HTTP server on a free loopback port that serves fixed pages, optionally after a delay,
/// and records the query strings it receives.
/// </summary>
internal sealed class TestHttpServer : IAsyncDisposable
{
  private readonly HttpListener _listener;
  private readonly Dictionary<string, Route> _routes = [];
  private readonly Lock _routesLock = new();
  private readonly Task _loop;

  public TestHttpServer()
  {
    (_listener, BaseUrl) = LoopbackHttpListener.Start();
    _loop = Task.Run(ServeAsync);
  }

  public string BaseUrl { get; }

  /// <summary>
  /// The query strings received per path, in arrival order.
  /// </summary>
  public ConcurrentDictionary<string, ConcurrentQueue<string>> Requests { get; } = new();

  /// <summary>
  /// Serves <paramref name="body"/> at <paramref name="path"/>, after <paramref name="delay"/> and
  /// once <paramref name="gate"/> has completed.
  /// </summary>
  public TestHttpServer Map(
    string path,
    string body,
    string contentType = "text/html; charset=utf-8",
    TimeSpan delay = default,
    Task? gate = null,
    string cacheControl = "no-store"
  )
  {
    lock (_routesLock)
    {
      _routes[path] = new Route(contentType, body, delay, gate ?? Task.CompletedTask, cacheControl);
    }

    return this;
  }

  public async ValueTask DisposeAsync()
  {
    _listener.Stop();
    _listener.Close();
    try
    {
      await _loop;
    }
    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
    {
      // Stopped.
    }
  }

  private async Task ServeAsync()
  {
    while (_listener.IsListening)
    {
      HttpListenerContext context;
      try
      {
        context = await _listener.GetContextAsync();
      }
      catch (Exception exception)
        when (exception
            is HttpListenerException
              or ObjectDisposedException
              or InvalidOperationException
        )
      {
        return;
      }

      _ = Task.Run(() => RespondAsync(context));
    }
  }

  private async Task RespondAsync(HttpListenerContext context)
  {
    var path = context.Request.Url!.AbsolutePath;
    Requests.GetOrAdd(path, _ => new()).Enqueue(context.Request.Url.Query.TrimStart('?'));

    Route? route;
    bool found;
    lock (_routesLock)
    {
      found = _routes.TryGetValue(path, out route);
    }

    try
    {
      if (!found)
      {
        context.Response.StatusCode = 404;
        context.Response.Close();
        return;
      }

      if (route!.Delay > TimeSpan.Zero)
      {
        await Task.Delay(route.Delay);
      }

      await route.Gate;

      var bytes = Encoding.UTF8.GetBytes(route.Body);
      context.Response.ContentType = route.ContentType;
      context.Response.Headers["Cache-Control"] = route.CacheControl;
      context.Response.ContentLength64 = bytes.Length;
      await context.Response.OutputStream.WriteAsync(bytes);
      context.Response.Close();
    }
    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
    {
      // The browser went away.
    }
  }

  private sealed record Route(
    string ContentType,
    string Body,
    TimeSpan Delay,
    Task Gate,
    string CacheControl
  );
}
