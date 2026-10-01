using Atli.Reports.Blazor.Models;

namespace Atli.Reports.Blazor.Extensions;

/// <summary>
/// Extension methods for <see cref="BlazorReport" />.
/// </summary>
public static class BlazorReportExtensions
{
  /// <summary>
  /// Gets the content type for the specified <see cref="BlazorReport" />.
  /// </summary>
  /// <param name="blazorReport"> The <see cref="BlazorReport" /> to get the content type for. </param>
  /// <returns> The content type for the specified <see cref="BlazorReport" />. </returns>
  public static string GetContentType(this BlazorReport blazorReport)
  {
    return blazorReport.OutputFormat switch
    {
      ReportOutputFormat.Pdf => "application/pdf",
      ReportOutputFormat.Html => "text/html",
      _ => "application/pdf",
    };
  }

  /// <summary>
  /// Gets the file extension for the specified <see cref="BlazorReport" />.
  /// </summary>
  /// <param name="blazorReport"> The <see cref="BlazorReport" /> to get the file extension for. </param>
  /// <returns> The file extension for the specified <see cref="BlazorReport" />. </returns>
  public static string GetFileExtension(this BlazorReport blazorReport)
  {
    return blazorReport.OutputFormat switch
    {
      ReportOutputFormat.Pdf => "pdf",
      ReportOutputFormat.Html => "html",
      _ => "pdf",
    };
  }
}
