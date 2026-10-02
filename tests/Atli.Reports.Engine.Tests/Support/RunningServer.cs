using System.Text;
using Atli.Reports.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// The reports server on a free loopback port, optionally with a replacement converter, so tests can
/// check the HTTP contract without a browser.
/// </summary>
internal sealed class RunningServer(WebApplication app) : IAsyncDisposable
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.First()) };

  public IServiceProvider Services => app.Services;

  public static async Task<RunningServer> StartAsync(
    IHtmlToPdfConverter? converter,
    params string[] arguments
  )
  {
    var app = ReportsServerApplication.Create(
      [
        "--urls=http://127.0.0.1:0",
        "--ReportsServer:Authentication:Mode=None",
        "--ReportsEngine:Browser:WarmUpOnStartup=false",
        "--ReportsEngine:Browser:NoSandbox=true",
        .. arguments,
      ],
      builder =>
      {
        builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
        if (converter is not null)
        {
          builder.Services.AddSingleton(converter);
        }
      }
    );
    await app.StartAsync(TestToken);
    return new RunningServer(app);
  }

  public Task<HttpResponseMessage> PostAsync(string json) =>
    Client.PostAsync(
      "/convert",
      new StringContent(json, Encoding.UTF8, "application/json"),
      TestToken
    );

  public async ValueTask DisposeAsync()
  {
    Client.Dispose();
    await app.StopAsync(CancellationToken.None);
    await app.DisposeAsync();
  }
}
