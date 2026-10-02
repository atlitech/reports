using System.Net;
using System.Net.Sockets;

namespace Atli.Reports.Engine.Chromium.Network;

/// <summary>
/// Fetches public allowlisted assets outside Chromium. Connections are made to validated numeric
/// addresses, so DNS is not looked up a second time after authorization. No browser credentials,
/// cookies, system proxies, or automatic redirects enter this HTTP client.
/// </summary>
internal sealed class AssetBroker : IDisposable
{
  private readonly ReportsEngineNetworkOptions _options;
  private readonly AssetNetworkPolicy _policy;
  private readonly HttpClient _client;
  private int _requests;
  private long _bytes;

  public AssetBroker(ReportsEngineNetworkOptions options, HttpMessageHandler? testHandler = null)
  {
    _options = options;
    _policy = new AssetNetworkPolicy(options);
    _client = new HttpClient(
      testHandler
        ?? new SocketsHttpHandler
        {
          UseProxy = false,
          UseCookies = false,
          ActivityHeadersPropagator = null,
          AllowAutoRedirect = false,
          AutomaticDecompression = DecompressionMethods.All,
          MaxResponseHeadersLength = 16,
          MaxConnectionsPerServer = 4,
          ConnectCallback = ConnectAsync,
        }
    )
    {
      Timeout = Timeout.InfiniteTimeSpan,
      DefaultRequestVersion = HttpVersion.Version11,
      DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };
  }

  public async Task<AssetResponse?> FetchAsync(
    Uri uri,
    string method,
    CancellationToken cancellationToken
  )
  {
    if (method is not ("GET" or "HEAD"))
    {
      return null;
    }

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(_options.RequestTimeout);
    for (var redirect = 0; redirect <= 5; redirect++)
    {
      if (!_policy.Allows(uri) || Interlocked.Increment(ref _requests) > _options.MaxRequests)
      {
        return null;
      }

      using HttpRequestMessage request = new(new HttpMethod(method), uri);
      using var response = await _client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        timeout.Token
      );

      if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
      {
        var location = response.Headers.Location;
        if (location is null || !Uri.TryCreate(uri, location, out var next))
        {
          return null;
        }

        uri = next;
        continue;
      }

      if (
        !response.IsSuccessStatusCode
        || response.Content.Headers.ContentLength > _options.MaxResponseBytes
      )
      {
        return null;
      }

      await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
      using MemoryStream body = new();
      var buffer = new byte[16 * 1024];
      while (true)
      {
        var read = await stream.ReadAsync(buffer, timeout.Token);
        if (read == 0)
        {
          break;
        }

        if (
          body.Length + read > _options.MaxResponseBytes
          || Interlocked.Add(ref _bytes, read) > _options.MaxTotalResponseBytes
        )
        {
          return null;
        }

        body.Write(buffer, 0, read);
      }

      return new AssetResponse(
        response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
        body.ToArray()
      );
    }

    return null;
  }

  internal static async ValueTask<Stream> ConnectAsync(
    SocketsHttpConnectionContext context,
    CancellationToken cancellationToken
  )
  {
    var addresses = await ResolvePublicAddressesAsync(
      context.DnsEndPoint.Host,
      Dns.GetHostAddressesAsync,
      cancellationToken
    );

    foreach (var address in addresses)
    {
      Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
      {
        NoDelay = true,
      };
      try
      {
        // Use the numeric endpoint directly. TLS still validates the original request hostname.
        await socket.ConnectAsync(
          new IPEndPoint(address, context.DnsEndPoint.Port),
          cancellationToken
        );
        return new NetworkStream(socket, ownsSocket: true);
      }
      catch (SocketException)
      {
        socket.Dispose();
      }
      catch
      {
        socket.Dispose();
        throw;
      }
    }

    throw new HttpRequestException("The asset destination could not be reached.");
  }

  internal static async Task<IPAddress[]> ResolvePublicAddressesAsync(
    string host,
    Func<string, CancellationToken, Task<IPAddress[]>> resolve,
    CancellationToken cancellationToken
  )
  {
    var addresses = IPAddress.TryParse(host, out var literal)
      ? [literal]
      : await resolve(host, cancellationToken);
    if (
      addresses.Length == 0
      || addresses.Any(address => !AssetNetworkPolicy.IsPublicAddress(address))
    )
    {
      // Reject mixed public/private answers too; do not silently pick an acceptable answer.
      throw new HttpRequestException("The asset destination is not a permitted public address.");
    }

    return addresses;
  }

  public void Dispose() => _client.Dispose();
}

internal sealed record AssetResponse(string ContentType, byte[] Body);
