namespace Atli.Reports.Engine;

/// <summary>
/// PDF page margins, in inches.
/// </summary>
public sealed record Margins
{
  /// <summary>
  /// The top margin. Defaults to 0.4 inches.
  /// </summary>
  public double Top { get; init; } = 0.4;

  /// <summary>
  /// The bottom margin. Defaults to 0.4 inches.
  /// </summary>
  public double Bottom { get; init; } = 0.4;

  /// <summary>
  /// The left margin. Defaults to 0.4 inches.
  /// </summary>
  public double Left { get; init; } = 0.4;

  /// <summary>
  /// The right margin. Defaults to 0.4 inches.
  /// </summary>
  public double Right { get; init; } = 0.4;

  /// <summary>
  /// 0.4 inch margins on every side.
  /// </summary>
  public static Margins Default => new();

  /// <summary>
  /// No margins.
  /// </summary>
  public static Margins None =>
    new()
    {
      Top = 0,
      Bottom = 0,
      Left = 0,
      Right = 0,
    };
}
