using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Atli.Reports.Engine.Chromium.Network;

/// <summary>
/// A context-wide fallback for targets not covered by page Fetch interception (workers, popups,
/// WebSockets). Never connects anywhere, parses nothing, and immediately closes every connection.
/// </summary>
internal sealed class DenyNetworkProxy : IAsyncDisposable
{
  private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
  private readonly CancellationTokenSource _stopping = new();
  private readonly Task _loop;
  private bool _rejected;

  public DenyNetworkProxy()
  {
    _listener.Start();
    Address = string.Create(
      CultureInfo.InvariantCulture,
      $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}"
    );
    _loop = RejectConnectionsAsync();
  }

  public string Address { get; }
  public bool HasRejected => Volatile.Read(ref _rejected);
  public event Action? Rejected;

  private async Task RejectConnectionsAsync()
  {
    try
    {
      while (!_stopping.IsCancellationRequested)
      {
        using var connection = await _listener.AcceptTcpClientAsync(_stopping.Token);
        Volatile.Write(ref _rejected, true);
        Rejected?.Invoke();
      }
    }
    catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    catch (SocketException) when (_stopping.IsCancellationRequested) { }
  }

  public async ValueTask DisposeAsync()
  {
    await _stopping.CancelAsync();
    _listener.Stop();
    await _loop;
    _stopping.Dispose();
  }
}
