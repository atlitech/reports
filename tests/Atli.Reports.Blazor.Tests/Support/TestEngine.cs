using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Support;

/// <summary>
/// Configures the reports for the machine the tests run on.
/// </summary>
/// <remarks>
/// The browser runs without its sandbox: Ubuntu 24.04 runners block the user namespaces the sandbox
/// needs, and the HTML here is trusted. Browser settings are configured directly on the engine.
/// </remarks>
internal static class TestEngine
{
  public static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(20);

  public static void Configure(ReportsEngineOptions options)
  {
    options.Browser.NoSandbox = true;
    options.Browser.DisableDevShmUsage = true;
    options.Browser.CommandTimeout = GenerousTimeout;
  }

  /// <summary>
  /// Builds a service provider with <c>AddBlazorReports</c>, for tests that call the services directly.
  /// </summary>
  public static ServiceProvider CreateServices(
    Action<BlazorReportOptions>? configureReports = null,
    Action<IServiceCollection>? configureServices = null
  )
  {
    ServiceCollection services = new();
    services.AddBlazorReports(configureReports);
    services.AddReportsEngine(Configure);
    configureServices?.Invoke(services);
    return services.BuildServiceProvider(
      new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
    );
  }
}
