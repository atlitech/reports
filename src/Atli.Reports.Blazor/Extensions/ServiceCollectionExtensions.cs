using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Services.BrowserServices;
using Atli.Reports.Blazor.Services.BrowserServices.Factories;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Extensions;

/// <summary>
/// Extension methods for <see cref="IServiceCollection" />.
/// </summary>
public static class ServiceCollectionExtensions
{
  /// <summary>
  /// Adds BlazorReports services to the specified <see cref="IServiceCollection" />.
  /// </summary>
  /// <param name="services"></param>
  /// <param name="options"></param>
  /// <returns> The <see cref="IServiceCollection" /> so that additional calls can be chained. </returns>
  public static IServiceCollection AddBlazorReports(
    this IServiceCollection services,
    Action<BlazorReportsOptions>? options = null
  )
  {
    services.Configure(options ?? (_ => { }));
    services.AddSingleton<BlazorReportRegistry>();
    services.AddSingleton<IConnectionFactory, ConnectionFactory>();
    services.AddSingleton<IBrowserFactory, BrowserFactory>();
    services.AddSingleton<IBrowserPageFactory, BrowserPageFactory>();
    services.AddSingleton<IBrowserService, BrowserService>();
    services.AddSingleton<IReportService, ReportService>();

    services.Configure<RouteOptions>(routeOptions =>
      routeOptions.SetParameterPolicy<RegexInlineRouteConstraint>("regex")
    );

    return services;
  }
}
