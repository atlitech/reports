using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>
/// Starts gateways and renderers for the gateway tests: both are the real server, the gateway in
/// <c>ReportsServer:Mode=Gateway</c> and each renderer in integrated mode behind its own API key.
/// </summary>
internal static class GatewayHost
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  /// <summary>
  /// Starts a gateway with anonymous callers (the caller ID <c>anonymous</c>) and HTTP renderers
  /// allowed; <paramref name="settings"/> come after the defaults and override them.
  /// </summary>
  public static async Task<RunningServer> StartAsync(
    string[] settings,
    Action<WebApplicationBuilder>? configure = null
  )
  {
    var app = ReportsServerApplication.Create(
      [
        "--urls=http://127.0.0.1:0",
        "--ReportsServer:Mode=Gateway",
        "--ReportsServer:Authentication:Mode=None",
        "--ReportsServer:Limits:MaxConcurrentRequestsPerCaller=64",
        "--ReportsServer:Gateway:AllowHttpRenderers=true",
        .. settings,
      ],
      builder =>
      {
        builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
        configure?.Invoke(builder);
      }
    );
    try
    {
      await app.StartAsync(TestToken);
      return new RunningServer(app);
    }
    catch
    {
      await app.DisposeAsync();
      throw;
    }
  }

  /// <summary>
  /// Starts a real renderer: the server in integrated mode, configured exactly as the provisioner
  /// configures a renderer (<see cref="RendererServerEnvironment"/>), so it admits only
  /// <paramref name="credential"/>.
  /// </summary>
  public static Task<RunningServer> StartRendererAsync(
    IHtmlToPdfConverter converter,
    RendererCredential credential
  ) =>
    RunningServer.StartAsync(
      converter,
      [
        .. RendererServerEnvironment
          .Create(credential, RendererSize.Medium)
          .Select(variable =>
            $"--{variable.Key.Replace("__", ":", StringComparison.Ordinal)}={variable.Value}"
          ),
      ]
    );

  /// <summary>A converter that writes <paramref name="pdf"/>.</summary>
  public static FakeConverter PdfConverter(string pdf) =>
    new(
      async (destination, cancellationToken) =>
      {
        await destination.WriteAsync(Encoding.ASCII.GetBytes(pdf), cancellationToken);
        return null;
      }
    );

  public static string[] Membership(int index, string callerId, params string[] tenants) =>
    [
      $"--ReportsServer:Gateway:Tenants:{index}:CallerId={callerId}",
      .. tenants.Select(
        (tenant, position) => $"--ReportsServer:Gateway:Tenants:{index}:Tenants:{position}={tenant}"
      ),
    ];

  /// <summary>A renderer in the gateway's configuration store.</summary>
  public static string[] Renderer(
    int index,
    string tenantId,
    string url,
    string apiKey,
    string? sandboxId = null
  ) =>
    [
      "--ReportsServer:Gateway:Records:Store=Configuration",
      $"--ReportsServer:Gateway:Records:Renderers:{index}:TenantId={tenantId}",
      $"--ReportsServer:Gateway:Records:Renderers:{index}:Url={url}",
      $"--ReportsServer:Gateway:Records:Renderers:{index}:ApiKey={apiKey}",
      .. sandboxId is null
        ? Array.Empty<string>()
        : [$"--ReportsServer:Gateway:Records:Renderers:{index}:SandboxId={sandboxId}"],
    ];

  /// <summary>The anonymous caller in one tenant, served by one renderer.</summary>
  public static string[] OneTenant(string url, string tenantId = "acme", string apiKey = TestKey) =>
    [.. Membership(0, "anonymous", tenantId), .. Renderer(0, tenantId, url, apiKey)];

  /// <summary>A renderer credential for tests that do not check it.</summary>
  public const string TestKey = "reports-000000000000.test-renderer-key";

  /// <summary>Waking through Sandboxes; the group settings only need to be complete.</summary>
  public static string[] SandboxesWake(string timeout = "00:00:10") =>
    [
      "--ReportsServer:Gateway:Wake:Mode=Sandboxes",
      $"--ReportsServer:Gateway:Wake:Timeout={timeout}",
      "--ReportsServer:Gateway:Wake:Sandboxes:SubscriptionId=00000000-0000-0000-0000-000000000000",
      "--ReportsServer:Gateway:Wake:Sandboxes:ResourceGroup=reports",
      "--ReportsServer:Gateway:Wake:Sandboxes:SandboxGroup=renderers",
      "--ReportsServer:Gateway:Wake:Sandboxes:Region=eastus2",
    ];

  /// <summary>Reads, checks, and disposes a problem details response.</summary>
  public static async Task<Problem> ReadProblemAsync(HttpResponseMessage response)
  {
    using var _ = response;
    await Assert
      .That(response.Content.Headers.ContentType?.MediaType)
      .IsEqualTo("application/problem+json");
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestToken));
    var root = json.RootElement;
    return new Problem(
      (int)response.StatusCode,
      root.GetProperty("kind").GetString()!,
      root.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
      root.TryGetProperty("title", out var title) ? title.GetString() : null,
      response.Headers.RetryAfter?.Delta
    );
  }
}

internal sealed record Problem(
  int Status,
  string Kind,
  string? Detail,
  string? Title,
  TimeSpan? RetryAfter
);

/// <summary>A record store whose records and failures a test controls, counting its calls.</summary>
internal sealed class FakeRecordStore : IRendererRecordStore
{
  private int _gets;
  private int _lists;

  public ConcurrentDictionary<string, RendererRecord> Records { get; } = new();

  /// <summary>When set, every call throws it.</summary>
  public Exception? Failure { get; set; }

  public int Gets => Volatile.Read(ref _gets);

  public int Lists => Volatile.Read(ref _lists);

  public Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
  {
    Interlocked.Increment(ref _gets);
    return Failure is { } failure
      ? Task.FromException<RendererRecord?>(failure)
      : Task.FromResult(Records.GetValueOrDefault(tenantId));
  }

  public Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken)
  {
    Interlocked.Increment(ref _lists);
    return Failure is { } failure
      ? Task.FromException<IReadOnlyList<RendererRecord>>(failure)
      : Task.FromResult<IReadOnlyList<RendererRecord>>([.. Records.Values]);
  }

  public Task PutAsync(RendererRecord record, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The gateway never writes records.");

  public Task DeleteAsync(string tenantId, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The gateway never deletes records.");
}

/// <summary>
/// A Sandboxes data plane that only resumes: each call runs the test's callback, which wakes the
/// fake platform or fails.
/// </summary>
internal sealed class FakeSandboxesClient(Func<string, CancellationToken, Task> onResume)
  : ISandboxesClient
{
  private int _resumes;

  public int Resumes => Volatile.Read(ref _resumes);

  public async Task<SandboxView> ResumeAsync(string sandboxId, CancellationToken cancellationToken)
  {
    Interlocked.Increment(ref _resumes);
    await onResume(sandboxId, cancellationToken);
    return new SandboxView { Id = sandboxId, State = SandboxStates.Running };
  }

  public Task<SandboxView> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The gateway only resumes sandboxes.");

  public Task<SandboxView?> GetAsync(string sandboxId, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The gateway only resumes sandboxes.");

  public Task<IReadOnlyList<SandboxView>> ListAsync(CancellationToken cancellationToken) =>
    throw new NotSupportedException("The gateway only resumes sandboxes.");

  public Task DeleteAsync(string sandboxId, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The gateway only resumes sandboxes.");

  public Task<SandboxView> StopAsync(string sandboxId, CancellationToken cancellationToken) =>
    throw new NotSupportedException("The gateway only resumes sandboxes.");

  public Task<SandboxView> AddPortAsync(
    string sandboxId,
    int port,
    bool anonymous,
    CancellationToken cancellationToken
  ) => throw new NotSupportedException("The gateway only resumes sandboxes.");
}

/// <summary>
/// A raw TCP "renderer" that reads one request (headers and its chunked body) and answers with
/// fixed bytes, for responses no HTTP server library would send.
/// </summary>
internal sealed class RawHttpResponder : IAsyncDisposable
{
  private readonly TcpListener _listener;
  private readonly Task _loop;

  public RawHttpResponder(string response)
  {
    _listener = new TcpListener(IPAddress.Loopback, 0);
    _listener.Start();
    BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
    _loop = ServeAsync(Encoding.ASCII.GetBytes(response));
  }

  public string BaseUrl { get; }

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
        // Read through the end of the chunked request body, so closing sends no reset.
        List<byte> received = [];
        var buffer = new byte[8192];
        while (!EndsWith(received, "\r\n0\r\n\r\n"u8))
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

  private static bool EndsWith(List<byte> bytes, ReadOnlySpan<byte> suffix) =>
    bytes.Count >= suffix.Length
    && System
      .Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[^suffix.Length..]
      .SequenceEqual(suffix);

  public async ValueTask DisposeAsync()
  {
    _listener.Stop();
    _listener.Dispose();
    await _loop;
  }
}

/// <summary>
/// A <see cref="FakeRenderer"/> serving tenant <c>acme</c> and a gateway in front of it, whose logs
/// the test can read. The caller is anonymous, a member of <c>acme</c> only.
/// </summary>
internal sealed class GatewayUnderTest : IAsyncDisposable
{
  private GatewayUnderTest(FakeRenderer renderer, RunningServer gateway, LogCollector logs)
  {
    Renderer = renderer;
    Gateway = gateway;
    Logs = logs;
  }

  public FakeRenderer Renderer { get; }

  public RunningServer Gateway { get; }

  public LogCollector Logs { get; }

  public static async Task<GatewayUnderTest> StartAsync(
    Func<HttpContext, Task> handler,
    string[]? settings = null,
    Action<WebApplicationBuilder>? configure = null,
    string? sandboxId = null
  )
  {
    var renderer = await FakeRenderer.StartAsync(handler);
    LogCollector logs = new();
    try
    {
      var gateway = await GatewayHost.StartAsync(
        [
          .. GatewayHost.Membership(0, "anonymous", "acme"),
          .. GatewayHost.Renderer(0, "acme", renderer.BaseUrl, GatewayHost.TestKey, sandboxId),
          .. settings ?? [],
        ],
        builder =>
        {
          builder.Logging.AddProvider(logs);
          configure?.Invoke(builder);
        }
      );
      return new GatewayUnderTest(renderer, gateway, logs);
    }
    catch
    {
      await renderer.DisposeAsync();
      throw;
    }
  }

  public Task<HttpResponseMessage> PostAsync(string json = """{"html":"<p>x</p>"}""") =>
    Gateway.PostAsync(json);

  /// <summary>Posts a conversion and returns once the response headers arrive.</summary>
  public async Task<HttpResponseMessage> PostForHeadersAsync(CancellationToken cancellationToken)
  {
    using HttpRequestMessage request = new(HttpMethod.Post, "/convert")
    {
      Content = new StringContent("""{"html":"<p>x</p>"}""", Encoding.UTF8, "application/json"),
    };
    return await Gateway.Client.SendAsync(
      request,
      HttpCompletionOption.ResponseHeadersRead,
      cancellationToken
    );
  }

  public async ValueTask DisposeAsync()
  {
    await Gateway.DisposeAsync();
    await Renderer.DisposeAsync();
  }
}
