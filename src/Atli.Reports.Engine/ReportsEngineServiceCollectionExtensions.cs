using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Configuration;
using Atli.Reports.Engine.Conversion;
using Atli.Reports.Engine.Health;
using Atli.Reports.Engine.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine;

/// <summary>
/// Registers the reports engine with an <see cref="IServiceCollection"/>.
/// </summary>
public static class ReportsEngineServiceCollectionExtensions
{
  /// <summary>
  /// Adds the reports engine and registers <see cref="IHtmlToPdfConverter"/> as a singleton.
  /// </summary>
  /// <param name="services">The service collection to add the engine to.</param>
  /// <param name="configure">Configures <see cref="ReportsEngineOptions"/>. <see langword="null"/> keeps the defaults.</param>
  /// <returns><paramref name="services"/>, for chaining.</returns>
  /// <remarks>
  /// <para>
  /// Calling this more than once registers the services once and applies every <paramref name="configure"/> action.
  /// </para>
  /// <para>
  /// The engine runs one long-lived browser process, shared by all conversions, and gives every
  /// conversion a page in a browser context of its own. The browser closes (process tree killed,
  /// temporary profile deleted) when the host stops or the service provider is disposed. The options
  /// are validated when they are first used, or at host start-up.
  /// </para>
  /// </remarks>
  public static IServiceCollection AddReportsEngine(
    this IServiceCollection services,
    Action<ReportsEngineOptions>? configure = null
  )
  {
    ArgumentNullException.ThrowIfNull(services);

    var optionsBuilder = services.AddOptions<ReportsEngineOptions>();
    if (configure is not null)
    {
      optionsBuilder.Configure(configure);
    }

    optionsBuilder.ValidateOnStart();
    services.TryAddEnumerable(
      ServiceDescriptor.Singleton<
        IValidateOptions<ReportsEngineOptions>,
        ReportsEngineOptionsValidator
      >()
    );

    services.AddLogging();
    services.TryAddSingleton(TimeProvider.System);
    services.TryAddSingleton<BrowserManager>();
    services.TryAddSingleton<IBrowserProvider>(provider =>
      provider.GetRequiredService<BrowserManager>()
    );
    services.TryAddSingleton<ConversionLimiter>();
    services.TryAddSingleton<ConversionHealthTracker>();
    services.TryAddSingleton<IHtmlToPdfConverter, HtmlToPdfConverter>();
    services.TryAddEnumerable(
      ServiceDescriptor.Singleton<IHostedService, ReportsEngineHostedService>()
    );

    return services;
  }

  /// <summary>
  /// Adds the reports engine, binds <see cref="ReportsEngineOptions"/> from <paramref name="configuration"/>,
  /// and registers <see cref="IHtmlToPdfConverter"/> as a singleton.
  /// </summary>
  /// <param name="services">The service collection to add the engine to.</param>
  /// <param name="configuration">
  /// The configuration section to bind, usually <c>configuration.GetSection(ReportsEngineOptions.SectionName)</c>.
  /// Keys mirror the option properties, for example <c>Browser:ExecutablePath</c> or <c>Browser:NoSandbox</c>.
  /// </param>
  /// <returns><paramref name="services"/>, for chaining.</returns>
  /// <remarks>Binding uses the configuration binding source generator, so it is safe in trimmed and NativeAOT apps.</remarks>
  public static IServiceCollection AddReportsEngine(
    this IServiceCollection services,
    IConfiguration configuration
  )
  {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.AddOptions<ReportsEngineOptions>().Bind(configuration);
    return services.AddReportsEngine();
  }
}
