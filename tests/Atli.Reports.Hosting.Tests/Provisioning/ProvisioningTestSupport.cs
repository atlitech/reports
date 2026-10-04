using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Atli.Reports.Hosting.Provisioning;
using Microsoft.Extensions.Time.Testing;

namespace Atli.Reports.Hosting.Tests.Provisioning;

/// <summary>
/// A <see cref="ProvisioningClient"/> over a <see cref="ScriptedHandler"/>, so the client runs whole
/// with no network.
/// </summary>
internal sealed class TestProvisioning : IDisposable
{
  public const string BaseAddress = "https://provisioner.internal.example.test/api/";

  public const string ApiKey = "gateway.0123456789abcdefghijklmnopqrstuvwxyzABCDEFG";

  public TestProvisioning(Func<HttpRequestMessage, HttpResponseMessage> answer)
  {
    Handler = new ScriptedHandler(answer);
    HttpClient = new HttpClient(Handler);
    Client = new ProvisioningClient(HttpClient, new Uri(BaseAddress), ApiKey, Time);
  }

  public FakeTimeProvider Time { get; } =
    new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));

  public ScriptedHandler Handler { get; }

  public HttpClient HttpClient { get; }

  public ProvisioningClient Client { get; }

  /// <summary>Answers with <paramref name="status"/> and <paramref name="body"/>.</summary>
  public static Func<HttpRequestMessage, HttpResponseMessage> Answer(
    HttpStatusCode status,
    string body = "",
    string contentType = "application/json",
    Action<HttpResponseMessage>? configure = null
  ) =>
    _ =>
    {
      HttpResponseMessage response = new(status)
      {
        Content = new StringContent(body, Encoding.UTF8, contentType),
      };
      configure?.Invoke(response);
      return response;
    };

  /// <summary>Answers with the service's problem details of <paramref name="kind"/>.</summary>
  public static Func<HttpRequestMessage, HttpResponseMessage> Problem(
    HttpStatusCode status,
    string kind,
    string detail = "Refused.",
    Action<HttpResponseMessage>? configure = null
  ) =>
    Answer(
      status,
      $$"""{"title":"Refused","status":{{(int)status}},"detail":"{{detail}}","kind":"{{kind}}"}""",
      "application/problem+json",
      configure
    );

  public void Dispose()
  {
    HttpClient.Dispose();
    Handler.Dispose();
  }
}

/// <summary>An <see cref="HttpMessageHandler"/> that records each request and answers it.</summary>
internal sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
  : HttpMessageHandler
{
  private readonly Lock _lock = new();
  private readonly List<HttpRequestMessage> _requests = [];

  public IReadOnlyList<HttpRequestMessage> Requests
  {
    get
    {
      lock (_lock)
      {
        return [.. _requests];
      }
    }
  }

  protected override Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request,
    CancellationToken cancellationToken
  )
  {
    lock (_lock)
    {
      _requests.Add(request);
    }

    return Task.FromResult(answer(request));
  }
}

/// <summary>
/// A raw HTTP/1.1 server on a free loopback port: reads each request's head (and its body, by its
/// <c>Content-Length</c>), records the head's lines, and answers with fixed bytes, so tests see
/// exactly which headers a real handler sent.
/// </summary>
internal sealed class RawHttpServer : IAsyncDisposable
{
  private readonly TcpListener _listener;
  private readonly Task _loop;
  private readonly Lock _lock = new();
  private readonly List<string[]> _heads = [];

  public RawHttpServer(string response)
  {
    _listener = new TcpListener(IPAddress.Loopback, 0);
    _listener.Start();
    BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
    _loop = ServeAsync(Encoding.ASCII.GetBytes(response));
  }

  /// <summary>The server's address, without a trailing slash.</summary>
  public string BaseUrl { get; }

  /// <summary>Each request's head: the request line, then one entry per header line.</summary>
  public IReadOnlyList<string[]> Heads
  {
    get
    {
      lock (_lock)
      {
        return [.. _heads];
      }
    }
  }

  /// <summary>A complete response that closes the connection.</summary>
  public static string Response(string statusLine, string headers = "", string body = "") =>
    $"HTTP/1.1 {statusLine}\r\n{headers}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

  private async Task ServeAsync(byte[] response)
  {
    while (true)
    {
      Socket socket;
      try
      {
        socket = await _listener.AcceptSocketAsync();
      }
      catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
      {
        return;
      }

      using (socket)
      {
        List<byte> received = [];
        var buffer = new byte[8192];
        int headEnd;
        while ((headEnd = IndexOf(received, "\r\n\r\n"u8)) < 0)
        {
          var read = await socket.ReceiveAsync(buffer);
          if (read == 0)
          {
            break;
          }

          received.AddRange(buffer.AsSpan(0, read));
        }

        if (headEnd < 0)
        {
          continue;
        }

        var lines = Encoding.ASCII.GetString([.. received.Take(headEnd)]).Split("\r\n");
        lock (_lock)
        {
          _heads.Add(lines);
        }

        // Read the body through, so closing sends no reset.
        var length = lines
          .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
          .Select(line =>
            int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture)
          )
          .FirstOrDefault();
        while (received.Count < headEnd + 4 + length)
        {
          var read = await socket.ReceiveAsync(buffer);
          if (read == 0)
          {
            break;
          }

          received.AddRange(buffer.AsSpan(0, read));
        }

        await socket.SendAsync(response);
        socket.Shutdown(SocketShutdown.Both);
      }
    }
  }

  private static int IndexOf(List<byte> bytes, ReadOnlySpan<byte> value) =>
    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes).IndexOf(value);

  public async ValueTask DisposeAsync()
  {
    _listener.Stop();
    _listener.Dispose();
    await _loop;
  }
}
