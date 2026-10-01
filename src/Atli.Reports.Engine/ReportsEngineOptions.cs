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
}
