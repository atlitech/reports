using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Server.Execution;

internal static class ReportsExecutionRegistration
{
  internal static void AddReportsExecution(this WebApplicationBuilder builder)
  {
    var options = new ReportsExecutionOptions();
    builder.Configuration.GetSection(ReportsExecutionOptions.SectionName).Bind(options);
    options.Validate();
    if (options.Mode == "Worker")
    {
      builder.Services.AddSingleton(options);
      builder.Services.AddSingleton<WorkerConverter>();
      builder.Services.TryAddSingleton<IHtmlToPdfConverter>(services =>
        services.GetRequiredService<WorkerConverter>()
      );
      builder
        .Services.AddHealthChecks()
        .AddCheck<WorkerExecutionHealthCheck>("worker_execution", tags: ["ready"]);
      return;
    }

    // Options were configured before the application's callback; registering the services now
    // preserves caller overrides and keeps all browser services absent from worker mode.
    builder.Services.AddReportsEngine();
    builder
      .Services.AddHealthChecks()
      .AddReportsEngineBrowserCheck("browser", tags: ["ready"])
      .AddReportsEngineConversionCheck("conversion_health", tags: ["ready"]);
  }

  private sealed class WorkerExecutionHealthCheck(WorkerConverter converter) : IHealthCheck
  {
    public Task<HealthCheckResult> CheckHealthAsync(
      HealthCheckContext context,
      CancellationToken cancellationToken = default
    ) => converter.CheckHealthAsync(cancellationToken);
  }
}
