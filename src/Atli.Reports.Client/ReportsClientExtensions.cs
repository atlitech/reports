using Atli.Reports.Client.Health;
using Atli.Reports.Client.Http;
using Atli.Reports.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;

namespace Atli.Reports.Client;

/// <summary>
/// Registers the Atli Reports server client as the app's <see cref="IHtmlToPdfConverter"/>.
/// </summary>
public static class ReportsClientExtensions
{
  /// <summary>
  /// The name of the health check that probes the reports server's readiness.
  /// </summary>
  public const string HealthCheckName = "reports_server";

  /// <summary>
  /// Registers <see cref="IHtmlToPdfConverter"/> as a client for the reports server named by the
  /// <paramref name="connectionName"/> connection string, in place of the in-process engine.
  /// </summary>
  /// <param name="builder">The host application builder.</param>
  /// <param name="connectionName">
  /// The name of the connection string, read from <c>ConnectionStrings:{connectionName}</c>. It is
  /// either <c>Endpoint=&lt;url&gt;</c>, as the Aspire hosting integration writes it, or a bare URL.
  /// </param>
  /// <param name="configureSettings">
  /// Adjusts the settings after they are read from the <see cref="ReportsClientSettings.DefaultConfigSectionName"/>
  /// configuration section and the connection string.
  /// </param>
  /// <returns>The builder of the client's <see cref="HttpClient"/>, to add handlers (authentication, say) or more configuration.</returns>
  /// <exception cref="InvalidOperationException">No valid endpoint is configured, or the client is already registered.</exception>
  /// <remarks>
  /// See <see cref="AddReportsClient(IServiceCollection, ReportsClientSettings)"/> for what is
  /// registered.
  /// </remarks>
  public static IHttpClientBuilder AddReportsClient(
    this IHostApplicationBuilder builder,
    string connectionName,
    Action<ReportsClientSettings>? configureSettings = null
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

    ReportsClientSettings settings = new();
    builder.Configuration.GetSection(ReportsClientSettings.DefaultConfigSectionName).Bind(settings);

    if (builder.Configuration.GetConnectionString(connectionName) is { Length: > 0 } connection)
    {
      try
      {
        settings.ParseConnectionString(connection);
      }
      catch (ArgumentException exception)
      {
        throw new InvalidOperationException(
          $"The connection string 'ConnectionStrings:{connectionName}' is invalid. {exception.Message}",
          exception
        );
      }
    }

    configureSettings?.Invoke(settings);

    if (settings.Endpoint is null)
    {
      throw new InvalidOperationException(
        $"The reports server endpoint is missing. Set the connection string 'ConnectionStrings:{connectionName}' "
          + $"('Endpoint=<url>' or a bare URL), or '{ReportsClientSettings.DefaultConfigSectionName}:Endpoint'."
      );
    }

    return builder.Services.AddReportsClient(settings);
  }

  /// <summary>
  /// Registers <see cref="IHtmlToPdfConverter"/> as a client for the reports server at
  /// <see cref="ReportsClientSettings.Endpoint"/>, in place of the in-process engine.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="settings">The client settings. They are read once, here.</param>
  /// <returns>The builder of the client's <see cref="HttpClient"/>, to add handlers (authentication, say) or more configuration.</returns>
  /// <exception cref="ArgumentException">The settings are invalid.</exception>
  /// <exception cref="InvalidOperationException">The client is already registered.</exception>
  /// <remarks>
  /// <para>
  /// Registers a singleton <see cref="IHtmlToPdfConverter"/> that posts to the server's
  /// <c>/convert</c> endpoint through a named <see cref="HttpClient"/> from
  /// <see cref="IHttpClientFactory"/>, with a resilience pipeline: retries of <c>503</c> answers
  /// (honoring <c>Retry-After</c>) and of transport failures, and the
  /// <see cref="ReportsClientSettings.AttemptTimeout"/> and <see cref="ReportsClientSettings.TotalTimeout"/>.
  /// Resilience handlers added to every client (such as the standard handler of Aspire's service
  /// defaults) are removed from this one.
  /// Unless <see cref="ReportsClientSettings.DisableHealthChecks"/> is set, it also adds the
  /// <see cref="HealthCheckName"/> health check, tagged <c>ready</c>, which probes the server's
  /// <c>/health/ready</c>.
  /// </para>
  /// <para>
  /// The client replaces the in-process engine whatever the order of the calls: a converter
  /// registered before is removed, <c>AddReportsEngine</c> called afterwards keeps the client
  /// (it only adds its converter when none is registered), and the engine's
  /// <see cref="ReportsEngineBrowserOptions.WarmUpOnStartup"/> is turned off. The engine's
  /// remaining services stay registered but idle, so an app that also calls <c>AddBlazorReports</c>
  /// renders its components locally, converts them on the server, and never starts a browser.
  /// </para>
  /// </remarks>
  public static IHttpClientBuilder AddReportsClient(
    this IServiceCollection services,
    ReportsClientSettings settings
  )
  {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(settings);

    var endpoint = ValidateEndpoint(settings.Endpoint, nameof(settings));
    var attemptTimeout = ValidateTimeout(
      settings.AttemptTimeout,
      nameof(ReportsClientSettings.AttemptTimeout),
      nameof(settings)
    );
    var totalTimeout = ValidateTimeout(
      settings.TotalTimeout,
      nameof(ReportsClientSettings.TotalTimeout),
      nameof(settings)
    );
    var healthCheckTimeout = ValidateTimeout(
      settings.HealthCheckTimeout,
      nameof(ReportsClientSettings.HealthCheckTimeout),
      nameof(settings)
    );
    var maxRetryAttempts = settings.MaxRetryAttempts;
    if (maxRetryAttempts < 0)
    {
      throw new ArgumentException(
        $"{nameof(ReportsClientSettings.MaxRetryAttempts)} must not be negative.",
        nameof(settings)
      );
    }

    if (services.Any(service => service.ServiceType == typeof(ReportsClientMarker)))
    {
      throw new InvalidOperationException("The reports client is already registered.");
    }

    services.AddSingleton<ReportsClientMarker>();

    var httpClient = services.AddHttpClient(
      ReportsServerConverter.HttpClientName,
      client =>
      {
        client.BaseAddress = endpoint;
        // The resilience pipeline owns the timeouts; HttpClient's default 100 seconds would cut
        // the attempts and retries short.
        client.Timeout = Timeout.InfiniteTimeSpan;
      }
    );
    // Apps with Aspire service defaults add the standard resilience handler to every client. Its
    // 10-second attempt timeout and retries of 500 and 504 would cut conversions short and repeat
    // ones that cannot succeed, so this client keeps only its own pipeline. (The library's own
    // RemoveAllResilienceHandlers does the same, but is still experimental.)
    httpClient.ConfigureAdditionalHttpMessageHandlers(
      static (handlers, _) =>
      {
        for (var index = handlers.Count - 1; index >= 0; index--)
        {
          if (handlers[index] is ResilienceHandler)
          {
            handlers.RemoveAt(index);
          }
        }
      }
    );
    httpClient.AddResilienceHandler(
      ReportsResilience.PipelineName,
      pipeline =>
        ReportsResilience.Configure(pipeline, totalTimeout, maxRetryAttempts, attemptTimeout)
    );

    services.RemoveAll<IHtmlToPdfConverter>();
    services.AddSingleton<IHtmlToPdfConverter, ReportsServerConverter>();

    // Warm-up is the only way the engine starts a browser without a conversion, and every
    // conversion now goes to the server.
    services.PostConfigure<ReportsEngineOptions>(options =>
      options.Browser.WarmUpOnStartup = false
    );

    if (!settings.DisableHealthChecks)
    {
      services
        .AddHealthChecks()
        .AddCheck<ReportsServerHealthCheck>(
          HealthCheckName,
          failureStatus: null,
          tags: ["ready"],
          timeout: healthCheckTimeout
        );
    }

    return httpClient;
  }

  private static Uri ValidateEndpoint(Uri? endpoint, string paramName)
  {
    if (
      endpoint is null
      || !ReportsClientSettings.TryCreateEndpoint(endpoint.OriginalString, out var absolute)
    )
    {
      throw new ArgumentException(
        $"{nameof(ReportsClientSettings.Endpoint)} must be an absolute http or https URL.",
        paramName
      );
    }

    // Relative paths resolve against the last segment only when it ends with a slash, so
    // https://gateway/reports + convert is https://gateway/reports/convert.
    return absolute.AbsolutePath.EndsWith('/')
      ? absolute
      : new UriBuilder(absolute) { Path = absolute.AbsolutePath + "/" }.Uri;
  }

  private static TimeSpan ValidateTimeout(TimeSpan timeout, string name, string paramName)
  {
    if (timeout < TimeSpan.FromMilliseconds(10) || timeout > TimeSpan.FromHours(24))
    {
      throw new ArgumentException(
        $"{name} must be between 10 milliseconds and 24 hours.",
        paramName
      );
    }

    return timeout;
  }

  /// <summary>
  /// Marks the service collection as holding the client, so a second registration fails clearly.
  /// </summary>
  private sealed class ReportsClientMarker;
}
