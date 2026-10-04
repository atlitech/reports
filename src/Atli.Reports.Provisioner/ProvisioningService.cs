using Atli.Reports.Hosting.Provisioning;
using Atli.Reports.Provisioner.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner;

/// <summary>
/// <c>serve</c>: the provisioning service's HTTP API (see <see cref="ProvisioningApi"/>) and its
/// retirement loop, until <c>cancellationToken</c> stops it.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core on Kestrel, over plain HTTP: the service belongs on internal ingress, behind TLS that
/// the platform terminates. The provisioner's settings come from the <c>Provisioner</c> section, as
/// for every command; ASP.NET Core's own, such as <c>ASPNETCORE_URLS</c> and <c>Logging__*</c>,
/// from its usual environment variables. It listens on <see cref="DefaultUrl"/> unless
/// <c>ASPNETCORE_URLS</c> or <c>ASPNETCORE_HTTP_PORTS</c> says otherwise.
/// </para>
/// <para>
/// Everything goes to the log, the provisioner's progress lines included. Log entries name tenants,
/// sandboxes, and disk images, never a key, a renderer's credential, or its verifier; an answer's
/// problem details never say why something failed.
/// </para>
/// </remarks>
internal static class ProvisioningService
{
  /// <summary>Where the service listens unless ASP.NET Core's settings say otherwise.</summary>
  public const string DefaultUrl = "http://+:8080";

  /// <summary>Runs the service; returns the process exit code once it has stopped.</summary>
  /// <param name="options">The provisioner's settings, <c>Service</c> validated with API keys.</param>
  /// <param name="services">
  /// The data plane, the record store, and the readiness probe. The service makes its own
  /// provisioner over them, which writes to the log rather than the console.
  /// </param>
  /// <param name="time">The clock of the limits, the retirement loop, and the provisioner.</param>
  /// <param name="cancellationToken">Stops the service gracefully.</param>
  public static async Task<int> RunAsync(
    ProvisionerOptions options,
    ProvisionerServices services,
    TimeProvider time,
    CancellationToken cancellationToken
  )
  {
    await using var app = Build(options, services, time);
    return await ServeAsync(app, cancellationToken);
  }

  /// <summary>
  /// Serves until <paramref name="cancellationToken"/> is canceled, then stops: no new requests,
  /// the creations in flight canceled, each deleting the sandbox it made, and the requests waiting
  /// for them answered <c>503</c>. Returns <c>0</c> once every creation has cleaned up.
  /// </summary>
  internal static async Task<int> ServeAsync(
    WebApplication app,
    CancellationToken cancellationToken
  )
  {
    var renderers = app.Services.GetRequiredService<ManagedRenderers>();
    await app.StartAsync(cancellationToken);
    await app.WaitForShutdownAsync(cancellationToken);
    // A cleanup may outlast the host's shutdown timeout; it has a bound of its own.
    await renderers.WhenIdleAsync();
    return 0;
  }

  /// <summary>Builds the service, which <see cref="ServeAsync"/> runs.</summary>
  /// <param name="configure">Adjusts the builder before the service is built; tests listen on loopback.</param>
  internal static WebApplication Build(
    ProvisionerOptions options,
    ProvisionerServices services,
    TimeProvider time,
    Action<WebApplicationBuilder>? configure = null
  )
  {
    var builder = WebApplication.CreateSlimBuilder(
      new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }
    );
    if (
      string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.ServerUrlsKey])
      && string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.HttpPortsKey])
      && string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.HttpsPortsKey])
    )
    {
      builder.WebHost.UseUrls(DefaultUrl);
    }

    builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

    // ASP.NET Core's own entries for every request, the health probes' included, are noise. As in
    // its templates, they are left out by default, under every other source, so that
    // Logging__LogLevel__Microsoft.AspNetCore still overrides it.
    builder.Configuration.Sources.Insert(
      0,
      new MemoryConfigurationSource
      {
        InitialData = new Dictionary<string, string?>
        {
          ["Logging:LogLevel:Microsoft.AspNetCore"] = nameof(LogLevel.Warning),
        },
      }
    );

    // Program.cs owns SIGTERM and Ctrl+C, and cancels the command's token on the first; the host
    // must not handle them too, or a second signal would no longer end the process at once.
    builder.Services.AddSingleton<IHostLifetime>(new CommandLifetime());

    builder.Services.AddSingleton(options);
    // One gate for the requests and the retirement loop alike.
    builder.Services.AddSingleton<TenantGate>();
    builder.Services.AddSingleton(provider => new ProgressLog(
      provider.GetRequiredService<ILogger<RendererProvisioner>>()
    ));
    builder.Services.AddSingleton(provider => new RendererProvisioner(
      services.Sandboxes,
      services.Records,
      services.Readiness,
      time,
      provider.GetRequiredService<ProgressLog>(),
      options,
      provider.GetRequiredService<TenantGate>()
    ));
    builder.Services.AddSingleton(_ => new TenantCensus(services.Records, time));
    builder.Services.AddSingleton(provider => new ManagedRenderers(
      options,
      provider.GetRequiredService<RendererProvisioner>(),
      provider.GetRequiredService<TenantCensus>(),
      provider.GetRequiredService<TenantGate>(),
      time,
      provider.GetRequiredService<ILogger<ManagedRenderers>>(),
      provider.GetRequiredService<IHostApplicationLifetime>()
    ));
    builder.Services.AddHostedService(provider => new RetirementLoop(
      options.Service,
      cancellation =>
        provider
          .GetRequiredService<RendererProvisioner>()
          .RetireIdleAsync(options.Service, cancellation),
      provider.GetRequiredService<TenantCensus>(),
      time,
      provider.GetRequiredService<ILogger<RetirementLoop>>()
    ));

    builder.Services.AddSingleton(new ServiceApiKeys(options.Service.ApiKeys));

    // Source-generated JSON only: a type the contexts do not know fails rather than reflects.
    builder.Services.ConfigureHttpJsonOptions(json =>
      json.SerializerOptions.TypeInfoResolver = ServiceJsonContext.Default
    );
    builder.Services.AddProblemDetails(problems =>
      problems.CustomizeProblemDetails = ServiceProblems.Customize
    );
    builder.Services.AddExceptionHandler(handler =>
      handler.StatusCodeSelector = exception =>
        exception is BadHttpRequestException badRequest
          ? badRequest.StatusCode
          : StatusCodes.Status503ServiceUnavailable
    );

    // Liveness asks only that the service answers; readiness, that the record store does too.
    builder
      .Services.AddHealthChecks()
      .AddCheck<RecordStoreHealthCheck>(
        "records",
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(10)
      );

    configure?.Invoke(builder);
    var app = builder.Build();
    app.UseExceptionHandler();
    app.UseStatusCodePages();
    // Every request but the health probes needs one of the gateway's keys, unknown routes included.
    app.UseMiddleware<GatewayAuthentication>();
    app.UseRouting();
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks(
      "/health/ready",
      new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }
    );
    app.MapRendererEndpoints();
    return app;
  }

  /// <summary>
  /// The host's lifetime under the command line: it leaves the process's signals to Program.cs and
  /// writes no banner. The command's cancellation token stops the host.
  /// </summary>
  private sealed class CommandLifetime : IHostLifetime
  {
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  }
}
