using Atli.Reports.Blazor.Models;

namespace Atli.Reports.Blazor.Tailwind;

/// <summary>
/// Selects stylesheets compiled by the Atli.Reports.Blazor.Tailwind build integration.
/// </summary>
public static class TailwindReportOptionsExtensions
{
  /// <summary>
  /// Uses a compiled Tailwind bundle for a report.
  /// </summary>
  /// <param name="options">The report registration options.</param>
  /// <param name="bundlePath">
  /// The project-relative input path without <c>.tailwind.css</c>, or the explicitly configured
  /// <c>BundlePath</c>. For example, <c>Reports/Invoice</c> selects <c>tailwind/Reports/Invoice.css</c>.
  /// </param>
  /// <param name="reloadOnChange">Whether to read the compiled stylesheet again for each render.</param>
  /// <returns>The supplied options.</returns>
  /// <remarks>
  /// Paths are resolved from <see cref="AppContext.BaseDirectory"/>, so rendering does not depend on
  /// the process's working directory. The stylesheet must already exist when the report is registered.
  /// This method does not compile CSS at runtime.
  /// </remarks>
  public static BlazorReportRegistrationOptions UseTailwind(
    this BlazorReportRegistrationOptions options,
    string bundlePath,
    bool reloadOnChange = false
  )
  {
    ArgumentNullException.ThrowIfNull(options);
    options.BaseStylesPath = GetStylesPath(bundlePath);
    options.BaseStylesReloadOnChange = reloadOnChange;
    return options;
  }

  /// <summary>
  /// Uses a compiled Tailwind bundle as the default stylesheet for reports without their own styles.
  /// </summary>
  /// <param name="options">The global report options.</param>
  /// <param name="bundlePath">
  /// The project-relative input path without <c>.tailwind.css</c>, or the explicitly configured
  /// <c>BundlePath</c>. For example, <c>Reports/Shared</c> selects <c>tailwind/Reports/Shared.css</c>.
  /// </param>
  /// <param name="reloadOnChange">Whether to read the compiled stylesheet again for each render.</param>
  /// <returns>The supplied options.</returns>
  /// <remarks>
  /// Paths are resolved from <see cref="AppContext.BaseDirectory"/>. The stylesheet must already
  /// exist when the report registry is initialized. This method does not compile CSS at runtime.
  /// </remarks>
  public static BlazorReportOptions UseTailwind(
    this BlazorReportOptions options,
    string bundlePath,
    bool reloadOnChange = false
  )
  {
    ArgumentNullException.ThrowIfNull(options);
    options.BaseStylesPath = GetStylesPath(bundlePath);
    options.BaseStylesReloadOnChange = reloadOnChange;
    return options;
  }

  private static string GetStylesPath(string bundlePath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
    var normalized = bundlePath.Replace('\\', '/');
    if (
      normalized.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
      || normalized.Split('/').Any(IsInvalidSegment)
    )
    {
      throw new ArgumentException(
        "Use a relative Tailwind bundle path without the .tailwind.css or .css suffix, for example 'Reports/Invoice'. Paths must not contain empty segments, '.', '..', reserved file names, or invalid file-name characters.",
        nameof(bundlePath)
      );
    }

    return Path.Combine(
      AppContext.BaseDirectory,
      "tailwind",
      normalized.Replace('/', Path.DirectorySeparatorChar) + ".css"
    );
  }

  private static bool IsInvalidSegment(string segment)
  {
    if (
      string.IsNullOrWhiteSpace(segment)
      || segment is "." or ".."
      || segment.EndsWith('.')
      || segment.EndsWith(' ')
      || segment.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character))
    )
    {
      return true;
    }

    var stem = segment.Split('.')[0];
    return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
      || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
      || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
      || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
      || (
        stem.Length == 4
        && (
          stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
          || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)
        )
        && stem[3] is >= '1' and <= '9'
      );
  }
}
