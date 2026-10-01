using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Protocol;
using Atli.Reports.Engine.Chromium.Protocol.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atli.Reports.Engine.Tests.Chromium;

/// <summary>
/// Drives <see cref="DevToolsConnection"/> against a fake DevTools peer over a real loopback
/// WebSocket, so framing, races, and failures can be staged precisely.
/// </summary>
public class DevToolsConnectionTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Replies_that_arrive_immediately_are_never_lost()
  {
    await using var pair = await FakePeer.ConnectAsync(TimeSpan.FromSeconds(10));
    var echo = pair.Peer.ReplyToEverythingAsync(id => Reply(id, $"{{\"n\":{id}}}"));

    var replies = await Task.WhenAll(
      Enumerable
        .Range(0, 200)
        .Select(async _ =>
        {
          using var reply = await pair.Connection.SendAsync(new("Test.echo"), null, TestToken);
          return reply.Result.Length;
        })
    );

    await Assert.That(replies.All(length => length > 0)).IsTrue();
    await Assert.That(pair.Connection.PendingCount).IsEqualTo(0);
    await pair.DisposeAsync();
    await echo;
  }

  [Test]
  public async Task Messages_split_over_many_frames_are_reassembled()
  {
    await using var pair = await FakePeer.ConnectAsync(TimeSpan.FromSeconds(10));
    var payload = new string('x', 300 * 1024);

    var send = pair.Connection.SendAsync(new("Test.big"), null, TestToken);
    var command = await pair.Peer.ReceiveAsync();
    var id = command.GetProperty("id").GetInt32();
    await pair.Peer.SendAsync(Reply(id, "{\"data\":\"" + payload + "\"}"), frameSize: 4096);
    using var reply = await send;

    var result = JsonElement.Parse(reply.Result);
    await Assert.That(result.GetProperty("data").GetString()).IsEqualTo(payload);
  }

  [Test]
  public async Task Large_commands_reach_the_peer_intact()
  {
    await using var pair = await FakePeer.ConnectAsync(TimeSpan.FromSeconds(10));
    var html = "<p>" + new string('é', 1024 * 1024) + "</p><script>a < b && c > d</script>";
    DevToolsMessage message = new("Page.setDocumentContent");
    message.Parameters.Add("html", html);
    var session = pair.Connection.AttachSession("session-1", "target-1");

    var send = session.SendAsync(message, TestToken);
    var command = await pair.Peer.ReceiveAsync();
    var id = command.GetProperty("id").GetInt32();
    await pair.Peer.SendAsync(Reply(id, """{}"""));
    using var reply = await send;

    var parameters = command.GetProperty("params");
    await Assert.That(parameters.GetProperty("html").GetString()).IsEqualTo(html);
    await Assert.That(command.GetProperty("sessionId").GetString()).IsEqualTo("session-1");
  }

  [Test]
  public async Task An_error_reply_throws_DevToolsProtocolException()
  {
    await using var pair = await FakePeer.ConnectAsync(TimeSpan.FromSeconds(10));

    var send = pair.Connection.SendAsync(new("Page.printToPDF"), null, TestToken);
    var command = await pair.Peer.ReceiveAsync();
    var id = command.GetProperty("id").GetInt32();
    await pair.Peer.SendAsync(
      ErrorReply(id, """{"code":-32000,"message":"Page range syntax error"}""")
    );

    var exception = await Assert.That(async () => await send).Throws<DevToolsProtocolException>();
    await Assert
      .That(exception!.Message)
      .IsEqualTo("Page.printToPDF failed: Page range syntax error (-32000)");
  }

  [Test]
  public async Task A_command_without_a_reply_times_out_and_the_connection_keeps_working()
  {
    await using var pair = await FakePeer.ConnectAsync(TimeSpan.FromMilliseconds(200));

    var unanswered = pair.Connection.SendAsync(new("Test.slow"), null, TestToken);
    var slow = await pair.Peer.ReceiveAsync();
    await Assert.That(async () => await unanswered).Throws<DevToolsTimeoutException>();
    await Assert.That(pair.Connection.PendingCount).IsEqualTo(0);

    // The late reply is dropped quietly, and the next command still works. The next command gets
    // its own generous timeout: the connection's 200 ms default exists to make the first command
    // time out, and a busy CI runner can take longer than that to answer.
    await pair.Peer.SendAsync(Reply(slow.GetProperty("id").GetInt32(), """{}"""));
    var next = pair.Connection.SendAsync(
      new("Test.next"),
      null,
      TestToken,
      TimeSpan.FromSeconds(10)
    );
    var command = await pair.Peer.ReceiveAsync();
    await pair.Peer.SendAsync(Reply(command.GetProperty("id").GetInt32(), """{"ok":true}"""));
    using var reply = await next;

    await Assert.That(Encoding.UTF8.GetString(reply.Result)).IsEqualTo("""{"ok":true}""");
  }

  [Test]
  public async Task A_dropped_connection_fails_pending_and_later_commands()
  {
    await using var pair = await FakePeer.ConnectAsync(TimeSpan.FromSeconds(10));

    var pending = pair.Connection.SendAsync(new("Test.pending"), null, TestToken);
    await pair.Peer.ReceiveAsync();
    pair.Peer.Abort();

    await Assert.That(async () => await pending).Throws<BrowserConnectionClosedException>();
    await pair.Connection.Closed.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(pair.Connection.IsOpen).IsFalse();
    await Assert
      .That(async () => await pair.Connection.SendAsync(new("Test.after"), null, TestToken))
      .Throws<BrowserConnectionClosedException>();
  }

  [Test]
  public async Task Session_events_reach_the_session_and_a_detach_fails_its_commands()
  {
    await using var pair = await FakePeer.ConnectAsync(TimeSpan.FromSeconds(10));
    var session = pair.Connection.AttachSession("session-1", "target-1");
    TaskCompletionSource<string> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
    session.EventReceived += (method, parameters) =>
      received.TrySetResult($"{method} {Encoding.UTF8.GetString(parameters)}");

    await pair.Peer.SendAsync(
      """{"method":"Runtime.bindingCalled","params":{"name":"x"},"sessionId":"session-1"}"""
    );
    var pending = session.SendAsync(new("Page.printToPDF"), TestToken);
    await pair.Peer.ReceiveAsync();
    await pair.Peer.SendAsync(
      """{"method":"Target.detachedFromTarget","params":{"sessionId":"session-1","targetId":"target-1"}}"""
    );

    await Assert
      .That(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)))
      .IsEqualTo("""Runtime.bindingCalled {"name":"x"}""");
    await Assert.That(async () => await pending).Throws<TargetCrashedException>();
    await Assert.That(async () => await session.Terminated).Throws<TargetCrashedException>();
  }

  [Test]
  public async Task A_malformed_message_does_not_stop_the_connection()
  {
    await using var pair = await FakePeer.ConnectAsync(TimeSpan.FromSeconds(10));

    var send = pair.Connection.SendAsync(new("Test.after"), null, TestToken);
    var command = await pair.Peer.ReceiveAsync();
    await pair.Peer.SendAsync("""{"id": "not a number" """);
    await pair.Peer.SendAsync(Reply(command.GetProperty("id").GetInt32(), """{}"""));
    using var reply = await send;

    await Assert.That(pair.Connection.IsOpen).IsTrue();
  }

  private static string Reply(int id, string result) => $"{{\"id\":{id},\"result\":{result}}}";

  private static string ErrorReply(int id, string error) => $"{{\"id\":{id},\"error\":{error}}}";

  /// <summary>
  /// A connection and the fake browser at the other end of its socket.
  /// </summary>
  private sealed class FakePeer(WebSocket socket, TcpClient client, TcpClient server)
  {
    public static async Task<Pair> ConnectAsync(TimeSpan commandTimeout)
    {
      TcpListener listener = new(IPAddress.Loopback, 0);
      listener.Start();
      TcpClient client = new();
      var connect = client.ConnectAsync(
        IPAddress.Loopback,
        ((IPEndPoint)listener.LocalEndpoint).Port
      );
      var server = await listener.AcceptTcpClientAsync();
      await connect;
      listener.Stop();

      var clientSocket = WebSocket.CreateFromStream(
        client.GetStream(),
        new WebSocketCreationOptions { IsServer = false }
      );
      var serverSocket = WebSocket.CreateFromStream(
        server.GetStream(),
        new WebSocketCreationOptions { IsServer = true }
      );
      var connection = DevToolsConnection.Create(
        clientSocket,
        new Uri("ws://127.0.0.1/devtools/browser/fake"),
        commandTimeout,
        NullLogger.Instance
      );
      return new Pair(connection, new FakePeer(serverSocket, client, server));
    }

    public async Task<JsonElement> ReceiveAsync()
    {
      using MemoryStream message = new();
      var buffer = new byte[64 * 1024];
      while (true)
      {
        var received = await socket.ReceiveAsync(buffer, TestToken);
        message.Write(buffer, 0, received.Count);
        if (received.EndOfMessage)
        {
          return JsonElement.Parse(
            new ReadOnlySpan<byte>(message.GetBuffer(), 0, (int)message.Length)
          );
        }
      }
    }

    public async Task SendAsync(string json, int? frameSize = null)
    {
      var bytes = Encoding.UTF8.GetBytes(json);
      var size = frameSize ?? bytes.Length;
      for (var offset = 0; offset < bytes.Length; offset += size)
      {
        var count = Math.Min(size, bytes.Length - offset);
        await socket.SendAsync(
          bytes.AsMemory(offset, count),
          WebSocketMessageType.Text,
          endOfMessage: offset + count == bytes.Length,
          TestToken
        );
      }
    }

    public async Task ReplyToEverythingAsync(Func<int, string> reply)
    {
      try
      {
        while (true)
        {
          var command = await ReceiveAsync();
          await SendAsync(reply(command.GetProperty("id").GetInt32()));
        }
      }
      catch (Exception exception)
        when (exception is WebSocketException or OperationCanceledException or IOException)
      {
        // The connection was disposed.
      }
    }

    public void Abort()
    {
      socket.Abort();
      server.Dispose();
    }

    public void Dispose()
    {
      socket.Dispose();
      client.Dispose();
      server.Dispose();
    }
  }

  private sealed class Pair(DevToolsConnection connection, FakePeer peer) : IAsyncDisposable
  {
    public DevToolsConnection Connection { get; } = connection;

    public FakePeer Peer { get; } = peer;

    public async ValueTask DisposeAsync()
    {
      await Connection.DisposeAsync();
      Peer.Dispose();
    }
  }
}
