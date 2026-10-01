using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Services.BrowserServices;
using Atli.Reports.Engine;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Blazor.Extensions;

/// <summary>
/// Extension methods for <see cref="IServiceCollection" />.
/// </summary>
public static class ServiceCollectionExtensions
{
  /// <summary>
  /// Adds the Blazor report services and Atli.Reports.Engine to the specified <see cref="IServiceCollection" />.
  /// </summary>
  /// <param name="services"> The service collection to add the services to. </param>
  /// <param name="options"> Configures <see cref="BlazorReportsOptions"/>. </param>
  /// <returns> The <see cref="IServiceCollection" /> so that additional calls can be chained. </returns>
  /// <remarks>
  /// <para>
  /// This registers <see cref="IHtmlToPdfConverter"/> through
  /// <see cref="ReportsEngineServiceCollectionExtensions.AddReportsEngine(IServiceCollection, Action{ReportsEngineOptions}?)"/>.
  /// Configure the engine further by calling <c>AddReportsEngine</c> yourself, for example to bind
  /// <see cref="ReportsEngineOptions"/> from configuration.
  /// </para>
  /// <para>
  /// Values set on <see cref="BlazorReportsOptions.BrowserOptions"/> that differ from their defaults are
  /// copied onto <see cref="ReportsEngineOptions.Browser"/>.
  /// </para>
  /// <para>
  /// To convert on an Atli Reports server instead, also call <c>AddReportsClient</c> from
  /// Atli.Reports.Client, before or after this method. Components then render in the app, their HTML
  /// is converted by the server, and the app never starts a browser.
  /// </para>
  /// </remarks>
  public static IServiceCollection AddBlazorReports(
    this IServiceCollection services,
    Action<BlazorReportsOptions>? options = null
  )
  {
    ArgumentNullException.ThrowIfNull(services);

    services.Configure(options ?? (_ => { }));
    services.AddReportsEngine();
    services
      .AddOptions<ReportsEngineOptions>()
      .Configure<IOptions<BlazorReportsOptions>>(
        (engineOptions, blazorReportsOptions) =>
          ApplyBrowserOptions(blazorReportsOptions.Value.BrowserOptions, engineOptions.Browser)
      );

    services.TryAddSingleton<BlazorReportRegistry>();
    services.TryAddSingleton<IReportService, ReportService>();
#pragma warning disable CS0618 // Keeps the obsolete IBrowserService resolvable for existing callers.
    services.TryAddSingleton<IBrowserService, EngineBrowserService>();
#pragma warning restore CS0618

    services.Configure<RouteOptions>(routeOptions =>
      routeOptions.SetParameterPolicy<RegexInlineRouteConstraint>("regex")
    );

    return services;
  }

  /// <summary>
  /// Copies the browser options that differ from their defaults onto the engine's browser options.
  /// </summary>
  internal static void ApplyBrowserOptions(
    BlazorReportsBrowserOptions browserOptions,
    ReportsEngineBrowserOptions engineBrowserOptions
  )
  {
    if (browserOptions.Browser == Browsers.Edge)
    {
      engineBrowserOptions.Kind = BrowserKind.Edge;
    }

    if (browserOptions.BrowserExecutableLocation is not null)
    {
      engineBrowserOptions.ExecutablePath = browserOptions.BrowserExecutableLocation.FullName;
    }

    if (browserOptions.NoSandbox)
    {
      engineBrowserOptions.NoSandbox = true;
    }

    if (browserOptions.DisableDevShmUsage)
    {
      engineBrowserOptions.DisableDevShmUsage = true;
    }

    if (browserOptions.DisableHeadless)
    {
      engineBrowserOptions.Headless = false;
    }

    if (browserOptions.ResponseTimeout != BlazorReportsBrowserOptions.DefaultResponseTimeout)
    {
      engineBrowserOptions.CommandTimeout = browserOptions.ResponseTimeout;
    }
  }
}
