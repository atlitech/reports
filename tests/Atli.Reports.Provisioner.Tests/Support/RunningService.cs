using System.Text.Json;
using Atli.Reports.Hosting.Provisioning;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Provisioner.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>
/// The provisioning service (<c>serve</c>) on a free loopback port over the provisioner's in-memory
/// fakes, with what it logs. It manages <c>myapp-</c> (at most 10 renderers, the default size) and
/// <c>big-</c> (size L), creates at most 20 renderers a minute under each and 60 in all, and admits
/// two gateway keys, <see cref="Gateway"/> and <see cref="Rotated"/>.
/// </summary>
internal sealed class RunningService : IAsyncDisposable
{
  /// <summary>The gateway's key; requests send it unless they say otherwise.</summary>
  public static readonly RendererCredential Gateway = RendererCredential.Generate();

  /// <summary>The gateway's next key, configured alongside for rotation.</summary>
  public static readonly RendererCredential Rotated = RendererCredential.Generate();

  private readonly WebApplication _app;
  private readonly CancellationTokenSource _stop;
  private readonly Task<int> _serving;

  private RunningService(
    WebApplication app,
    CancellationTokenSource stop,
    Task<int> serving,
    LogCapture logs
  )
  {
    _app = app;
    _stop = stop;
    _serving = serving;
    Logs = logs;
    Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
  }

  public HttpClient Client { get; }

  public LogCapture Logs { get; }

  /// <summary>What <see cref="ProvisioningService.ServeAsync"/> returned, once stopped.</summary>
  public Task<int> Serving => _serving;

  /// <summary>
  /// Starts the service. <paramref name="configure"/> adjusts its settings, which are then
  /// validated as <c>serve</c> validates them; <paramref name="records"/> replaces the record store.
  /// </summary>
  public static async Task<RunningService> StartAsync(
    Provisioning provisioning,
    Action<ProvisioningServiceOptions>? configure = null,
    IRendererRecordStore? records = null
  )
  {
    var options = provisioning.Options;
    options.DiskImageId = "disk-1";
    options.Service.ApiKeys.AddRange([Key(Gateway), Key(Rotated)]);
    options.Service.TenantPrefixes.AddRange([
      new ManagedTenantPrefix { Prefix = "myapp-", MaxTenants = 10 },
      new ManagedTenantPrefix { Prefix = "big-", Size = "L" },
    ]);
    // The gateway's address, as serve requires.
    options.AllowedSourceCidrs.Add("203.0.113.7/32");
    configure?.Invoke(options.Service);
    options.ValidateServe();

    LogCapture logs = new();
    var app = ProvisioningService.Build(
      options,
      new ProvisionerServices(
        provisioning.Sandboxes,
        records ?? provisioning.Records,
        provisioning.Readiness
      ),
      provisioning.Clock,
      builder =>
      {
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        // Over the service's own default, so that a test sees every entry.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Trace);
        builder.Logging.AddProvider(logs);
      }
    );

    TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using var registration = app.Lifetime.ApplicationStarted.Register(() => started.SetResult());
    CancellationTokenSource stop = new();
    var serving = ProvisioningService.ServeAsync(app, stop.Token);
    // A service that cannot start fails here.
    await await Task.WhenAny(started.Task, serving);
    // So that a test starts from the census's first listing, whether it succeeded or not, unless
    // the test holds it.
    if (records is not ListingRecordStore { HoldListing: not null })
    {
      await app.Services.GetRequiredService<TenantCensus>().FirstListing;
    }

    return new RunningService(app, stop, serving, logs);
  }

  /// <summary>Sends a request with <paramref name="key"/> in the API key header, or none.</summary>
  public async Task<HttpResponseMessage> SendAsync(
    HttpMethod method,
    string path,
    string? key,
    CancellationToken cancellationToken
  )
  {
    using HttpRequestMessage request = new(method, path);
    if (key is not null)
    {
      request.Headers.TryAddWithoutValidation(ProvisioningApi.ApiKeyHeader, key);
    }

    return await Client.SendAsync(request, cancellationToken);
  }

  /// <summary><c>PUT /tenants/{tenantId}/renderer</c> with the gateway's key.</summary>
  public Task<HttpResponseMessage> PutAsync(string tenantId, CancellationToken cancellationToken) =>
    SendAsync(
      HttpMethod.Put,
      $"/tenants/{tenantId}/renderer",
      Gateway.Credential,
      cancellationToken
    );

  /// <summary><c>DELETE /tenants/{tenantId}/renderer</c> with the gateway's key.</summary>
  public Task<HttpResponseMessage> DeleteAsync(
    string tenantId,
    CancellationToken cancellationToken
  ) =>
    SendAsync(
      HttpMethod.Delete,
      $"/tenants/{tenantId}/renderer",
      Gateway.Credential,
      cancellationToken
    );

  /// <summary>
  /// Lets the census's next listing start, <see cref="TenantCensus.RefreshInterval"/> on, and waits
  /// until it has ended, logged as <paramref name="eventId"/>: 40 for a listing that succeeds, 41
  /// for one that fails.
  /// </summary>
  public async Task ListAgainAsync(TestClock clock, int eventId)
  {
    var ended = Logs.Of<TenantCensus>(eventId).Count;
    await clock.WaitForTimerAsync(TenantCensus.RefreshInterval);
    clock.Advance(TenantCensus.RefreshInterval);
    await Logs.WaitForAsync<TenantCensus>(eventId, ended + 1);
  }

  /// <summary>Stops the service as a <c>SIGTERM</c> does, and returns its exit code.</summary>
  public async Task<int> StopAsync()
  {
    await _stop.CancelAsync();
    return await _serving;
  }

  /// <summary>An answer's body as JSON.</summary>
  public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
  {
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return document.RootElement.Clone();
  }

  public async ValueTask DisposeAsync()
  {
    Client.Dispose();
    if (!_stop.IsCancellationRequested)
    {
      await StopAsync();
    }

    await _app.DisposeAsync();
    _stop.Dispose();
    Logs.Dispose();
  }

  private static ProvisioningServiceApiKey Key(RendererCredential credential) =>
    new() { Id = credential.KeyId, Hash = credential.Verifier };
}
