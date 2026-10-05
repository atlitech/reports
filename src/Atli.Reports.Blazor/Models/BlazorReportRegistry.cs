using Atli.Reports.Blazor.Helpers;
using Atli.Reports.Engine;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Blazor.Models;

/// <summary>
/// The BlazorReportRegistry is a singleton that holds all of the BlazorReport objects.
/// </summary>
/// <remarks>
/// Base styles and assets are read from disk and encoded once, when the registry is created (global ones)
/// or when a report is registered (per-report ones), and then reused for every render. Each path is read
/// once per registry: reports that share a styles file or an assets folder share the loaded content. Edits
/// to those files after registration are not picked up until the application restarts.
/// </remarks>
public class BlazorReportRegistry
{
  private readonly Lock _loadLock = new();
  private readonly Dictionary<string, string> _stylesByPath = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Dictionary<string, string>> _assetsByPath = new(
    StringComparer.Ordinal
  );

  /// <summary>
  /// The BlazorReportRegistry is a singleton that holds all of the BlazorReport objects.
  /// </summary>
  /// <param name="options"> The BlazorReportOptions object that contains the configuration for the BlazorReportRegistry. </param>
  public BlazorReportRegistry(IOptions<BlazorReportOptions> options)
  {
    if (!string.IsNullOrWhiteSpace(options.Value.BaseStylesPath))
    {
      BaseStyles = LoadStyles(options.Value.BaseStylesPath);
    }

    if (!string.IsNullOrWhiteSpace(options.Value.AssetsPath))
    {
      GlobalAssets = LoadAssets(options.Value.AssetsPath);
    }

    ValidatePdfOptions(options.Value.PdfOptions);
    DefaultPdfOptions = options.Value.PdfOptions.Clone();
  }

  /// <summary>
  /// The PDF options copied when registering a report, before applying its configuration.
  /// </summary>
  public PdfOptions DefaultPdfOptions { get; set; }

  /// <summary>
  /// The base styles for the BlazorReportRegistry.
  /// </summary>
  public string BaseStyles { get; set; } = string.Empty;

  /// <summary>
  /// The global assets for the BlazorReportRegistry, keyed by file name, with base64 data URIs as values.
  /// </summary>
  public Dictionary<string, string> GlobalAssets { get; set; } = [];

  /// <summary>
  /// The BlazorReport objects for the BlazorReportRegistry.
  /// </summary>
  public Dictionary<string, BlazorReport> Reports { get; } = [];

  /// <summary>
  /// Adds a report to the BlazorReportRegistry.
  /// </summary>
  /// <param name="setupAction"> Configures a report after copying the global PDF defaults. </param>
  /// <typeparam name="T"> The type of the report to add. </typeparam>
  /// <returns> The BlazorReport that was added. </returns>
  /// <exception cref="InvalidOperationException"> Thrown when a report with the same name already exists. </exception>
  public BlazorReport AddReport<T>(Action<BlazorReportRegistrationOptions>? setupAction = null)
  {
    var component = typeof(T);
    BlazorReportRegistrationOptions options = new() { PdfOptions = DefaultPdfOptions.Clone() };
    setupAction?.Invoke(options);
    ValidatePdfOptions(options.PdfOptions);
    var reportNameToUse = options.ReportName ?? component.Name;
    var normalizedReportName = reportNameToUse.ToLowerInvariant().Trim();

    lock (_loadLock)
    {
      if (Reports.ContainsKey(normalizedReportName))
      {
        throw new InvalidOperationException(
          $"Report with name {normalizedReportName} already exists"
        );
      }

      BlazorReport blazorReport = new()
      {
        OutputFormat = options.OutputFormat,
        Name = reportNameToUse,
        NormalizedName = normalizedReportName,
        Component = component,
        PdfOptions = options.PdfOptions.Clone(),
      };
      if (!string.IsNullOrEmpty(options.BaseStylesPath))
      {
        blazorReport.BaseStyles = LoadStyles(options.BaseStylesPath);
      }

      if (!string.IsNullOrEmpty(options.AssetsPath))
      {
        blazorReport.Assets = LoadAssets(options.AssetsPath);
      }

      Reports.Add(normalizedReportName, blazorReport);
      return blazorReport;
    }
  }

  private static void ValidatePdfOptions(PdfOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    if (options.WaitForSignal is not null && string.IsNullOrWhiteSpace(options.WaitForSignal))
    {
      throw new ArgumentException("The signal name must not be blank.", nameof(options));
    }

    if (options.WaitTimeout < TimeSpan.Zero && options.WaitTimeout != Timeout.InfiniteTimeSpan)
    {
      throw new ArgumentOutOfRangeException(
        nameof(options),
        options.WaitTimeout,
        "The signal wait timeout must not be negative, except Timeout.InfiniteTimeSpan."
      );
    }
  }

  /// <summary>
  /// Returns the content of the styles file at <paramref name="path"/>, reading it on first use.
  /// </summary>
  private string LoadStyles(string path)
  {
    var fullPath = Path.GetFullPath(path);
    lock (_loadLock)
    {
      if (!_stylesByPath.TryGetValue(fullPath, out var styles))
      {
        styles = File.ReadAllText(fullPath);
        _stylesByPath.Add(fullPath, styles);
      }

      return styles;
    }
  }

  /// <summary>
  /// Returns the files in the folder at <paramref name="path"/> as base64 data URIs keyed by file name,
  /// reading and encoding them on first use. A folder that does not exist has no assets.
  /// </summary>
  /// <remarks>
  /// Each caller gets its own dictionary, so changing one report's assets does not change another's,
  /// but the encoded strings, which are the bulk of the memory, are shared.
  /// </remarks>
  private Dictionary<string, string> LoadAssets(string path)
  {
    var fullPath = Path.GetFullPath(path);
    lock (_loadLock)
    {
      if (!_assetsByPath.TryGetValue(fullPath, out var assets))
      {
        assets = [];
        DirectoryInfo assetsDirectory = new(fullPath);
        if (assetsDirectory.Exists)
        {
          foreach (var file in assetsDirectory.GetFiles())
          {
            var contentType = MimeTypes.GetMimeType(file.Name);
            var fileBytes = File.ReadAllBytes(file.FullName);
            assets.Add(file.Name, $"data:{contentType};base64,{Convert.ToBase64String(fileBytes)}");
          }
        }

        _assetsByPath.Add(fullPath, assets);
      }

      return new Dictionary<string, string>(assets);
    }
  }
}
