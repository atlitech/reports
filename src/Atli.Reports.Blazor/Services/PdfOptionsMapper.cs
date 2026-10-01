using Atli.Reports.Blazor.Enums;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Engine;

namespace Atli.Reports.Blazor.Services;

/// <summary>
/// Maps report page settings onto the engine's <see cref="PdfOptions"/>.
/// </summary>
internal static class PdfOptionsMapper
{
  public static PdfOptions Map(BlazorReportsPageSettings pageSettings)
  {
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
    };
  }
}
