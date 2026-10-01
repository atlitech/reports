using Atli.Reports.Blazor.Helpers;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Blazor.Models;

/// <summary>
/// The BlazorReportRegistry is a singleton that holds all of the BlazorReport objects.
/// </summary>
public class BlazorReportRegistry
{
  /// <summary>
  /// The BlazorReportRegistry is a singleton that holds all of the BlazorReport objects.
  /// </summary>
  /// <param name="options"> The BlazorReportsOptions object that contains the configuration for the BlazorReportRegistry. </param>
  public BlazorReportRegistry(IOptions<BlazorReportsOptions> options)
  {
    if (!string.IsNullOrWhiteSpace(options.Value.BaseStylesPath))
    {
      BaseStyles = File.ReadAllText(options.Value.BaseStylesPath);
    }

    if (!string.IsNullOrWhiteSpace(options.Value.AssetsPath))
    {
      var assetsPath = options.Value.AssetsPath;
      DirectoryInfo assetsDirectory = new(assetsPath);
      if (assetsDirectory.Exists)
      {
        foreach (var file in assetsDirectory.GetFiles())
        {
          var contentType = MimeTypes.GetMimeType(file.Name);
          var fileBytes = File.ReadAllBytes(file.FullName);
          var base64Uri = $"data:{contentType};base64,{Convert.ToBase64String(fileBytes)}";
          GlobalAssets.Add(file.Name, base64Uri);
        }
      }
    }

    DefaultPageSettings = options.Value.PageSettings;
  }

  /// <summary>
  /// The default page settings for the BlazorReports
  /// </summary>
  public BlazorReportsPageSettings DefaultPageSettings { get; set; }

  /// <summary>
  /// The base styles for the BlazorReportRegistry.
  /// </summary>
  public string BaseStyles { get; set; } = string.Empty;

  /// <summary>
  /// The global assets for the BlazorReportRegistry.
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
    var reportNameToUse = options?.ReportName ?? typeof(T).Name;
    var normalizedReportName = reportNameToUse.ToLowerInvariant().Trim();

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
      Component = typeof(T),
      Data = null,
      PageSettings = options?.PageSettings,
    };
    if (!string.IsNullOrEmpty(options?.BaseStylesPath))
    {
      blazorReport.BaseStyles = File.ReadAllText(options.BaseStylesPath);
    }
    if (!string.IsNullOrEmpty(options?.AssetsPath))
    {
      var assetsPath = options.AssetsPath;
      DirectoryInfo assetsDirectory = new(assetsPath);
      if (assetsDirectory.Exists)
      {
        foreach (var file in assetsDirectory.GetFiles())
        {
          var contentType = MimeTypes.GetMimeType(file.Name);
          var fileBytes = File.ReadAllBytes(file.FullName);
          var base64Uri = $"data:{contentType};base64,{Convert.ToBase64String(fileBytes)}";
          blazorReport.Assets.Add(file.Name, base64Uri);
        }
      }
    }
    Reports.Add(normalizedReportName, blazorReport);
    return blazorReport;
  }

  /// <summary>
  /// Adds a report to the BlazorReportRegistry.
  /// </summary>
  /// <param name="options"> The options to use when adding the report. </param>
  /// <typeparam name="T"> The type of the report to add. </typeparam>
  /// <typeparam name="TD"> The type of the data to use for the report. </typeparam>
  /// <returns> The BlazorReport that was added. </returns>
  /// <exception cref="InvalidOperationException"> Thrown when a report with the same name already exists. </exception>
  public BlazorReport AddReport<T, TD>(BlazorReportRegistrationOptions? options = null)
  {
    var reportNameToUse = options?.ReportName ?? typeof(T).Name;
    var normalizedReportName = reportNameToUse.ToLowerInvariant().Trim();

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
      Component = typeof(T),
      Data = typeof(TD),
      PageSettings = options?.PageSettings,
    };
    if (!string.IsNullOrEmpty(options?.BaseStylesPath))
    {
      blazorReport.BaseStyles = File.ReadAllText(options.BaseStylesPath);
    }
    if (!string.IsNullOrEmpty(options?.AssetsPath))
    {
      var assetsPath = options.AssetsPath;
      DirectoryInfo assetsDirectory = new(assetsPath);
      if (assetsDirectory.Exists)
      {
        foreach (var file in assetsDirectory.GetFiles())
        {
          var contentType = MimeTypes.GetMimeType(file.Name);
          var fileBytes = File.ReadAllBytes(file.FullName);
          var base64Uri = $"data:{contentType};base64,{Convert.ToBase64String(fileBytes)}";
          blazorReport.Assets.Add(file.Name, base64Uri);
        }
      }
    }
    Reports.Add(normalizedReportName, blazorReport);
    return blazorReport;
  }
}
