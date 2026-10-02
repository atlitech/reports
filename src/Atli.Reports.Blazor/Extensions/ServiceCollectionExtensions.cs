using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
  /// <param name="options"> Configures <see cref="BlazorReportOptions"/>. </param>
  /// <returns> The <see cref="IServiceCollection" /> so that additional calls can be chained. </returns>
  /// <remarks>
  /// <para>
  /// This registers <see cref="IHtmlToPdfConverter"/> through
  /// <see cref="ReportsEngineServiceCollectionExtensions.AddReportsEngine(IServiceCollection, Action{ReportsEngineOptions}?)"/>.
  /// Configure the engine further by calling <c>AddReportsEngine</c> yourself, for example to bind
  /// <see cref="ReportsEngineOptions"/> from configuration.
  /// </para>
  /// <para>
  /// To convert on an Atli Reports server instead, also call <c>AddReportsClient</c> from
  /// Atli.Reports.Client, before or after this method. Components then render in the app, their HTML
  /// is converted by the server, and the app never starts a browser.
  /// </para>
  /// </remarks>
  public static IServiceCollection AddBlazorReports(
    this IServiceCollection services,
    Action<BlazorReportOptions>? options = null
  )
  {
    ArgumentNullException.ThrowIfNull(services);

    services.Configure(options ?? (_ => { }));
    services.AddReportsEngine();
    services.TryAddSingleton<BlazorReportRegistry>();
    services.TryAddSingleton<IReportService, ReportService>();

    return services;
  }
}
