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
  /// Adds a check that reports whether the engine can launch its browser. It reports unhealthy while
  /// the most recent launch has failed, with the reason (the browser exited at once, did not report
  /// its DevTools endpoint in time, or could not be found), until a launch succeeds; and while the
  /// engine shuts down. It reports healthy while the browser runs and, when none runs (before the
  /// first conversion, say), as long as the browser executable exists. Suited to readiness probes.
  /// </summary>
  /// <param name="builder">The health checks builder.</param>
  /// <param name="name">The name of the check. Defaults to <see cref="BrowserCheckName"/>.</param>
  /// <param name="failureStatus">The status to report when the check fails. <see langword="null"/> reports <see cref="HealthStatus.Unhealthy"/>.</param>
  /// <param name="tags">Tags used to filter the check, for example <c>["ready"]</c>.</param>
  /// <returns><paramref name="builder"/>, for chaining.</returns>
  /// <remarks>
  /// <para>
  /// After a failed launch, the engine retries in the background (after 1 second, doubling up to 30
  /// seconds) until a launch succeeds, so an instance that a failing readiness probe keeps from
  /// getting conversions still recovers once the cause goes away.
  /// </para>
  /// <para>
  /// Keep the check out of liveness probes: restarting the host does not repair a browser that
  /// cannot start, and a slow start under load would get a healthy host killed. The failure reason
  /// quotes the browser's output, reduced to one line of at most 500 characters with URL credentials
  /// and secret-looking parameters masked.
  /// </para>
  /// </remarks>
  public static IHealthChecksBuilder AddReportsEngineBrowserCheck(
    this IHealthChecksBuilder builder,
    string name = BrowserCheckName,
    HealthStatus? failureStatus = null,
    IEnumerable<string>? tags = null
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    return builder.AddCheck<BrowserHealthCheck>(name, failureStatus, tags ?? []);
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
  /// Conversions rejected as <see cref="ConversionErrorKind.InvalidRequest"/>, canceled by the caller
  /// (<see cref="ConversionErrorKind.Canceled"/>), or shed under load
  /// (<see cref="ConversionErrorKind.Busy"/>) do not count as failures.
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
