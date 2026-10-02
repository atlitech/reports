using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Blazor.Tests.Support;

/// <summary>
/// A report server on Kestrel, listening on a free loopback port, with an <see cref="HttpClient"/> for it.
/// </summary>
internal sealed class TestReportServer : IAsyncDisposable
{
  private readonly WebApplication _app;

  private TestReportServer(WebApplication app, HttpClient client)
  {
    _app = app;
    Client = client;
  }

  public HttpClient Client { get; }

  public IServiceProvider Services => _app.Services;

  /// <summary>
  /// Starts a server whose services are configured by <paramref name="configureServices"/> (after
  /// <c>AddBlazorReports</c> with <paramref name="configureReports"/>) and whose endpoints are mapped by
  /// <paramref name="mapReports"/>.
  /// </summary>
  public static async Task<TestReportServer> StartAsync(
    Action<WebApplication> mapReports,
    Action<BlazorReportOptions>? configureReports = null,
    Action<IServiceCollection>? configureServices = null
  )
  {
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    builder.Services.AddBlazorReports(configureReports);
    builder.Services.AddReportsEngine(TestEngine.Configure);
    configureServices?.Invoke(builder.Services);

    var app = builder.Build();
    mapReports(app);
    await app.StartAsync(TestContext.Current!.Execution.CancellationToken);

    var address = app.Urls.Single();
    HttpClient client = new() { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(1) };
    return new TestReportServer(app, client);
  }

  public async ValueTask DisposeAsync()
  {
    Client.Dispose();
    await _app.StopAsync();
    await _app.DisposeAsync();
  }
}
