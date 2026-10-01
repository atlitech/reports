namespace Atli.Reports.Engine;

/// <summary>
/// Configures the reports engine.
/// </summary>
/// <remarks>
/// Set these through
/// <see cref="ReportsEngineServiceCollectionExtensions.AddReportsEngine(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{ReportsEngineOptions}?)"/>
/// or bind them from configuration with
/// <see cref="ReportsEngineServiceCollectionExtensions.AddReportsEngine(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration)"/>.
/// Per-conversion settings live on <see cref="PdfOptions"/> instead.
/// </remarks>
public sealed class ReportsEngineOptions
{
  /// <summary>
  /// The conventional configuration section name for these options: <c>ReportsEngine</c>.
  /// </summary>
  public const string SectionName = "ReportsEngine";

  /// <summary>
  /// How the engine finds, launches, and talks to the browser.
  /// </summary>
  public ReportsEngineBrowserOptions Browser { get; } = new();

  /// <summary>
  /// How many conversions run at once and how many may wait for a turn.
  /// </summary>
  public ReportsEngineConcurrencyOptions Concurrency { get; } = new();

  /// <summary>
  /// The longest one conversion may take as a whole, from the call until the last PDF byte is written,
  /// including its wait in the queue. Defaults to <see cref="Timeout.InfiniteTimeSpan"/>: no overall
  /// limit beyond the individual timeouts.
  /// </summary>
  /// <remarks>
  /// A conversion that runs out of time fails with <see cref="ConversionErrorKind.Timeout"/>, whatever it
  /// was doing at that moment (waiting for a turn, loading, waiting for a signal, printing, or
  /// streaming). Services that answer HTTP requests can set it below their clients' timeouts so overload
  /// ends in a clean error instead of a dropped connection. Must be greater than zero, or infinite.
  /// </remarks>
  public TimeSpan ConversionTimeout { get; set; } = Timeout.InfiniteTimeSpan;
}
