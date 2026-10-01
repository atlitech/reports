using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Support;

/// <summary>
/// Configures the reports for the machine the tests run on.
/// </summary>
/// <remarks>
/// The browser runs without its sandbox: Ubuntu 24.04 runners block the user namespaces the sandbox
/// needs, and the HTML here is trusted. The settings go through <see cref="BlazorReportsBrowserOptions"/>
/// so the tests also cover how those options reach the engine.
/// </remarks>
internal static class TestEngine
{
  public static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(20);

  public static void Configure(BlazorReportsOptions options)
  {
    options.BrowserOptions.NoSandbox = true;
    options.BrowserOptions.DisableDevShmUsage = true;
    options.BrowserOptions.ResponseTimeout = GenerousTimeout;
  }

  /// <summary>
  /// Builds a service provider with <c>AddBlazorReports</c>, for tests that call the services directly.
  /// </summary>
  public static ServiceProvider CreateServices(
    Action<BlazorReportsOptions>? configureReports = null,
    Action<IServiceCollection>? configureServices = null
  )
  {
    ServiceCollection services = new();
    services.AddBlazorReports(options =>
    {
      Configure(options);
      configureReports?.Invoke(options);
    });
    configureServices?.Invoke(services);
    return services.BuildServiceProvider(
      new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
    );
  }
}
