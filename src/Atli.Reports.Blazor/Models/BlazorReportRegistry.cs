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
/// to those files after registration are not picked up until the application restarts, unless
/// <c>BaseStylesReloadOnChange</c> is enabled for the corresponding stylesheet.
/// </remarks>
public class BlazorReportRegistry
{
  private string _baseStyles = string.Empty;
  private Func<string>? _baseStylesProvider;
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
      var path = Path.GetFullPath(options.Value.BaseStylesPath);
      BaseStyles = options.Value.BaseStylesReloadOnChange ? ReadStyles(path) : LoadStyles(path);
      if (options.Value.BaseStylesReloadOnChange)
      {
        _baseStylesProvider = () => ReadStyles(path);
      }
    }

    if (!string.IsNullOrWhiteSpace(options.Value.AssetsPath))
    {
      GlobalAssets = LoadAssets(options.Value.AssetsPath);
    }

    DefaultPdfOptions = options.Value.PdfOptions;
    options.Value.JavaScriptSettings.Validate(nameof(options));
    DefaultJavaScriptSettings = options.Value.JavaScriptSettings;
  }

  /// <summary>
  /// The default PDF conversion options for reports registered without their own.
  /// </summary>
  public PdfOptions DefaultPdfOptions { get; set; }

  /// <summary>
  /// The JavaScript settings for reports registered without their own.
  /// </summary>
  public BlazorReportJavaScriptOptions DefaultJavaScriptSettings { get; set; }

  /// <summary>
  /// The loaded global base styles. Setting this value overrides file-based styles,
  /// including styles configured to reload during development.
  /// </summary>
  public string BaseStyles
  {
    get => _baseStyles;
    set
    {
      _baseStyles = value;
      _baseStylesProvider = null;
    }
  }

  internal string ResolveBaseStyles() => _baseStylesProvider?.Invoke() ?? BaseStyles;

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
  /// <param name="options"> The options to use when adding the report. </param>
  /// <typeparam name="T"> The type of the report to add. </typeparam>
  /// <returns> The BlazorReport that was added. </returns>
  /// <exception cref="InvalidOperationException"> Thrown when a report with the same name already exists. </exception>
  public BlazorReport AddReport<T>(BlazorReportRegistrationOptions? options = null)
  {
    return AddReport(typeof(T), options);
  }

  private BlazorReport AddReport(Type component, BlazorReportRegistrationOptions? options)
  {
    options?.JavaScriptSettings.Validate(nameof(options));
    var reportNameToUse = options?.ReportName ?? component.Name;
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
        OutputFormat = options?.OutputFormat ?? ReportOutputFormat.Pdf,
        Name = reportNameToUse,
        NormalizedName = normalizedReportName,
        Component = component,
        PdfOptions = options?.PdfOptions,
        JavaScriptSettings = options?.JavaScriptSettings,
      };
      if (!string.IsNullOrEmpty(options?.BaseStylesPath))
      {
        var path = Path.GetFullPath(options.BaseStylesPath);
        blazorReport.BaseStyles = options.BaseStylesReloadOnChange
          ? ReadStyles(path)
          : LoadStyles(path);
        if (options.BaseStylesReloadOnChange)
        {
          blazorReport.BaseStylesProvider = () => ReadStyles(path);
        }
      }

      if (!string.IsNullOrEmpty(options?.AssetsPath))
      {
        blazorReport.Assets = LoadAssets(options.AssetsPath);
      }

      Reports.Add(normalizedReportName, blazorReport);
      return blazorReport;
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
        styles = ReadStyles(fullPath);
        _stylesByPath.Add(fullPath, styles);
      }

      return styles;
    }
  }

  private static string ReadStyles(string fullPath)
  {
    try
    {
      return File.ReadAllText(fullPath);
    }
    catch (IOException exception)
      when (exception is FileNotFoundException or DirectoryNotFoundException)
    {
      throw new FileNotFoundException(
        $"Report stylesheet '{fullPath}' was not found. Ensure the CSS is compiled and included in the application's build or publish output, and check BaseStylesPath.",
        fullPath,
        exception
      );
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
