using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// A stand-in for a renderer (or for the platform proxy in front of one) on a free loopback port:
/// a bare Kestrel app whose <c>POST /convert</c> runs the test's handler, after recording the
/// request's headers and body. Tests use it for renderers that misbehave in ways the real server
/// never does.
/// </summary>
internal sealed class FakeRenderer : IAsyncDisposable
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  private readonly WebApplication _app;

  private FakeRenderer(WebApplication app, Func<HttpContext, Task> handler)
  {
    _app = app;
    Handler = handler;
  }

  /// <summary>The renderer's base address, without a trailing slash.</summary>
  public string BaseUrl => _app.Urls.First();

  /// <summary>What the renderer's <c>/convert</c> does; tests may replace it.</summary>
  public Func<HttpContext, Task> Handler { get; set; }

  /// <summary>Every <c>/convert</c> request received, in arrival order.</summary>
  public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

  public static async Task<FakeRenderer> StartAsync(Func<HttpContext, Task> handler)
  {
    var builder = WebApplication.CreateSlimBuilder(
      new WebApplicationOptions { Args = ["--urls=http://127.0.0.1:0"] }
    );
    builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
    builder.Logging.ClearProviders();
    var app = builder.Build();
    FakeRenderer renderer = new(app, handler);
    app.MapPost(
      "/convert",
      async context =>
      {
        using StreamReader reader = new(context.Request.Body);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        renderer.Requests.Enqueue(
          new RecordedRequest(
            context.Request.Headers.ToDictionary(
              header => header.Key,
              header => header.Value.ToString(),
              StringComparer.OrdinalIgnoreCase
            ),
            body
          )
        );
        await renderer.Handler(context);
      }
    );
    await app.StartAsync(TestToken);
    return renderer;
  }

  /// <summary>Answers with <paramref name="body"/> as a PDF.</summary>
  public static async Task WritePdfAsync(HttpContext context, string body)
  {
    context.Response.ContentType = "application/pdf";
    await context.Response.WriteAsync(body, context.RequestAborted);
  }

  /// <summary>Answers with problem details, as the server does.</summary>
  public static async Task WriteProblemAsync(HttpContext context, int status, string json)
  {
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsync(json, context.RequestAborted);
  }

  public async ValueTask DisposeAsync()
  {
    await _app.StopAsync(CancellationToken.None);
    await _app.DisposeAsync();
  }
}

/// <summary>A request a <see cref="FakeRenderer"/> received.</summary>
internal sealed record RecordedRequest(IReadOnlyDictionary<string, string> Headers, string Body);
