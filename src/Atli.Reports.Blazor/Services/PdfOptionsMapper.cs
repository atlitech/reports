using Atli.Reports.Blazor.Enums;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Engine;

namespace Atli.Reports.Blazor.Services;

/// <summary>
/// Maps report page and JavaScript settings onto the engine's <see cref="PdfOptions"/>.
/// </summary>
internal static class PdfOptionsMapper
{
  /// <summary>
  /// The name of the function the engine exposes to reports that wait for their JavaScript. The template's
  /// <c>blazorReport.completed()</c> calls it, so reports never use the name directly.
  /// </summary>
  public const string CompletedSignalName = "atliReportCompleted";

  public static PdfOptions Map(
    BlazorReportsPageSettings pageSettings,
    BlazorReportsJavaScriptSettings? javaScriptSettings = null
  )
  {
    var waitForSignal = javaScriptSettings?.WaitForCompletedSignal == true;
    return new PdfOptions
    {
      Orientation =
        pageSettings.Orientation == BlazorReportsPageOrientation.Landscape
          ? PageOrientation.Landscape
          : PageOrientation.Portrait,
      Margins = new Margins
      {
        Top = pageSettings.MarginTop,
        Bottom = pageSettings.MarginBottom,
        Left = pageSettings.MarginLeft,
        Right = pageSettings.MarginRight,
      },
      PaperSize = new PaperSize
      {
        Width = pageSettings.PaperWidth,
        Height = pageSettings.PaperHeight,
      },
      PrintBackground = !pageSettings.IgnoreBackground,
      WaitForSignal = waitForSignal ? CompletedSignalName : null,
      WaitTimeout = waitForSignal
        ? javaScriptSettings!.CompletedSignalTimeout
        : BlazorReportsJavaScriptSettings.DefaultCompletedSignalTimeout,
    };
  }
}
