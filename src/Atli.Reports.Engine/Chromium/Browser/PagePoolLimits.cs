namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// Limits of the per-browser page pool.
/// </summary>
/// <remarks>
/// These are not configurable: the engine launches one browser per conversion, so a pool never holds
/// more than one page and the limits have no observable effect yet.
/// </remarks>
internal sealed record PagePoolLimits
{
  /// <summary>
  /// Maximum number of pages that can be created per browser instance
  /// </summary>
  public int MaxPagePoolSize { get; init; } = 10;

  /// <summary>
  /// Maximum number of times a page can be reused before disposal (0 = unlimited)
  /// </summary>
  public int MaxPageUsageCount { get; init; } = 100;

  /// <summary>
  /// Maximum age of a page before disposal (TimeSpan.Zero = unlimited)
  /// </summary>
  public TimeSpan MaxPageAge { get; init; } = TimeSpan.FromMinutes(5);

  public static PagePoolLimits Default { get; } = new();
}
