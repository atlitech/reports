using Atli.Reports.Engine.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Atli.Reports.Engine;

/// <summary>
/// Registers reports engine health checks with an <see cref="IHealthChecksBuilder"/>.
/// </summary>
/// <remarks>
/// Both checks need the engine itself, registered with
/// <see cref="ReportsEngineServiceCollectionExtensions.AddReportsEngine(IServiceCollection, Action{ReportsEngineOptions}?)"/>.
/// </remarks>
public static class ReportsEngineHealthChecksBuilderExtensions
{
  /// <summary>
  /// The default name of the check added by <see cref="AddReportsEngineBrowserCheck"/>.
  /// </summary>
  public const string BrowserCheckName = "reports_engine_browser";

  /// <summary>
  /// The default name of the check added by <see cref="AddReportsEngineConversionCheck"/>.
  /// </summary>
  public const string ConversionCheckName = "reports_engine_conversions";

  /// <summary>
  /// Adds a check that reports healthy when the configured browser executable exists, and unhealthy
  /// when it cannot be found. Suited to liveness and readiness probes.
  /// </summary>
  /// <param name="builder">The health checks builder.</param>
  /// <param name="name">The name of the check. Defaults to <see cref="BrowserCheckName"/>.</param>
  /// <param name="failureStatus">The status to report when the check fails. <see langword="null"/> reports <see cref="HealthStatus.Unhealthy"/>.</param>
  /// <param name="tags">Tags used to filter the check, for example <c>["live", "ready"]</c>.</param>
  /// <returns><paramref name="builder"/>, for chaining.</returns>
  public static IHealthChecksBuilder AddReportsEngineBrowserCheck(
    this IHealthChecksBuilder builder,
    string name = BrowserCheckName,
    HealthStatus? failureStatus = null,
    IEnumerable<string>? tags = null
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    return builder.AddCheck<BrowserExecutableHealthCheck>(name, failureStatus, tags ?? []);
  }

  /// <summary>
  /// Adds a check based on the outcomes of recent conversions. It reports unhealthy when at least three
  /// conversions finished in the last two minutes and fewer than half of them succeeded, or when fewer
  /// than three finished and the last three conversions failed. It reports healthy otherwise, including
  /// when there were no recent conversions. Suited to readiness probes.
  /// </summary>
  /// <param name="builder">The health checks builder.</param>
  /// <param name="name">The name of the check. Defaults to <see cref="ConversionCheckName"/>.</param>
  /// <param name="failureStatus">The status to report when the check fails. <see langword="null"/> reports <see cref="HealthStatus.Unhealthy"/>.</param>
  /// <param name="tags">Tags used to filter the check, for example <c>["ready"]</c>.</param>
  /// <returns><paramref name="builder"/>, for chaining.</returns>
  /// <remarks>
  /// Conversions rejected as <see cref="ConversionErrorKind.InvalidRequest"/> or
  /// <see cref="ConversionErrorKind.Canceled"/> by the caller do not count as failures.
  /// </remarks>
  public static IHealthChecksBuilder AddReportsEngineConversionCheck(
    this IHealthChecksBuilder builder,
    string name = ConversionCheckName,
    HealthStatus? failureStatus = null,
    IEnumerable<string>? tags = null
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    return builder.AddCheck<ConversionHealthCheck>(name, failureStatus, tags ?? []);
  }
}
