namespace Atli.Reports.Engine;

/// <summary>
/// A PDF paper size, in inches.
/// </summary>
public sealed record PaperSize
{
  /// <summary>
  /// The paper width, in inches.
  /// </summary>
  public double Width { get; init; }

  /// <summary>
  /// The paper height, in inches.
  /// </summary>
  public double Height { get; init; }

  /// <summary>
  /// US Letter, 8.5 × 11 inches.
  /// </summary>
  public static PaperSize Letter => new() { Width = 8.5, Height = 11 };

  /// <summary>
  /// US Legal, 8.5 × 14 inches.
  /// </summary>
  public static PaperSize Legal => new() { Width = 8.5, Height = 14 };

  /// <summary>
  /// ISO A4, 8.27 × 11.69 inches.
  /// </summary>
  public static PaperSize A4 => new() { Width = 8.27, Height = 11.69 };

  /// <summary>
  /// ISO A3, 11.69 × 16.54 inches.
  /// </summary>
  public static PaperSize A3 => new() { Width = 11.69, Height = 16.54 };
}
