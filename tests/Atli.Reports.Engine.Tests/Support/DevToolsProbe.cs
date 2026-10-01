using System.Text.Json;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Protocol.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// A second DevTools client on the engine's browser, so tests can watch the pages a conversion opens
/// and crash them, independently of the engine.
/// </summary>
internal sealed class DevToolsProbe(DevToolsConnection connection) : IAsyncDisposable
{
  public static async Task<DevToolsProbe> ConnectAsync(BrowserInstance browser) =>
    new(
      await DevToolsConnection.ConnectAsync(
        browser.Endpoint,
        TimeSpan.FromSeconds(30),
        NullLogger.Instance,
        TestContext.Current!.Execution.CancellationToken
      )
    );

  /// <summary>
  /// Waits until a page titled <paramref name="title"/> exists and returns its target id.
  /// </summary>
  public async Task<string> WaitForPageAsync(string title, TimeSpan timeout)
  {
    using CancellationTokenSource deadline = new(timeout);
    while (true)
    {
      using (var reply = await connection.SendAsync(new("Target.getTargets"), null, deadline.Token))
      {
        var targets = JsonElement.Parse(reply.Result);
        foreach (var target in targets.GetProperty("targetInfos").EnumerateArray())
        {
          if (target.GetProperty("title").GetString() == title)
          {
            return target.GetProperty("targetId").GetString()!;
          }
        }
      }

      await Task.Delay(50, deadline.Token);
    }
  }

  /// <summary>
  /// Crashes the renderer of the page <paramref name="targetId"/>.
  /// </summary>
  public async Task CrashAsync(string targetId)
  {
    DevToolsMessage attach = new("Target.attachToTarget");
    attach.Parameters.Add("targetId", targetId);
    attach.Parameters.Add("flatten", true);
    string sessionId;
    using (
      var reply = await connection.SendAsync(
        attach,
        null,
        TestContext.Current!.Execution.CancellationToken
      )
    )
    {
      sessionId = JsonElement.Parse(reply.Result).GetProperty("sessionId").GetString()!;
    }

    connection.AttachSession(sessionId, targetId);
    connection.Post(new DevToolsMessage("Page.crash"), sessionId);
  }

  public ValueTask DisposeAsync() => connection.DisposeAsync();
}
