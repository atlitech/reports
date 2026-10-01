using System.Net;
using System.Net.Sockets;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// Starts an <see cref="HttpListener"/> on a free loopback port.
/// </summary>
/// <remarks>
/// <see cref="HttpListener"/> cannot bind port 0, so the port comes from a probe that binds it and
/// lets it go. Anything else in the test run that binds an ephemeral port in between can take it,
/// and the listener then fails to start (address already in use); a fresh probe finds another port.
/// </remarks>
internal static class LoopbackHttpListener
{
  private const int MaxAttempts = 10;

  /// <summary>
  /// A started listener for <c>http://127.0.0.1:{port}/</c>, and its base URL without the trailing
  /// slash.
  /// </summary>
  public static (HttpListener Listener, string BaseUrl) Start()
  {
    for (var attempt = 1; ; attempt++)
    {
      var baseUrl = $"http://127.0.0.1:{FreePort()}";
      HttpListener listener = new();
      listener.Prefixes.Add(baseUrl + "/");
      try
      {
        listener.Start();
        return (listener, baseUrl);
      }
      catch (HttpListenerException) when (attempt < MaxAttempts)
      {
        // The probed port was taken before the listener bound it.
        listener.Close();
      }
    }
  }

  private static int FreePort()
  {
    using TcpListener probe = new(IPAddress.Loopback, 0);
    probe.Start();
    return ((IPEndPoint)probe.LocalEndpoint).Port;
  }
}
