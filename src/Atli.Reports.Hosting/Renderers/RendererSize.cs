using System.Diagnostics.CodeAnalysis;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// A renderer size: the sandbox's CPU and memory, and how many conversions it runs at once. The
/// 2026-10-03 Sandboxes run converted the 49-page report at every size here
/// (benchmarks/results/2026-10-03-5b667b4-azure-sandboxes-amd64.md).
/// </summary>
/// <remarks>
/// Measured in the sandboxes: throughput tops out at about one conversion per vCPU, and each 49-page
/// report in flight costs about 400 to 500 MiB, so a 2 GiB renderer running four to eight at once was
/// killed out of memory. Sizes run at most one conversion per vCPU, and at least one.
/// </remarks>
/// <param name="Name">The size's name: <c>S</c>, <c>M</c>, or <c>L</c>.</param>
/// <param name="Cpu">CPU in the platform's notation.</param>
/// <param name="Memory">Memory in the platform's notation.</param>
/// <param name="MaxConcurrentConversions">Conversions the renderer runs at once.</param>
public sealed record RendererSize(
  string Name,
  string Cpu,
  string Memory,
  int MaxConcurrentConversions
)
{
  /// <summary>0.5 vCPU and 1 GiB: one conversion at a time.</summary>
  public static RendererSize Small { get; } = new("S", "500m", "1024Mi", 1);

  /// <summary>1 vCPU and 2 GiB, one conversion at a time: the default.</summary>
  public static RendererSize Medium { get; } = new("M", "1000m", "2048Mi", 1);

  /// <summary>2 vCPU and 4 GiB, two conversions at a time.</summary>
  public static RendererSize Large { get; } = new("L", "2000m", "4096Mi", 2);

  /// <summary>Returns the size named <paramref name="name"/> (case-insensitive).</summary>
  public static RendererSize Parse(string name) =>
    TryParse(name, out var size)
      ? size
      : throw new ArgumentException(
        $"Unknown renderer size '{name}'. Use S, M, or L.",
        nameof(name)
      );

  /// <summary>Finds the named size (case-insensitive), without throwing for an unknown name.</summary>
  public static bool TryParse(string? name, [NotNullWhen(true)] out RendererSize? size)
  {
    size = name?.ToUpperInvariant() switch
    {
      "S" => Small,
      "M" => Medium,
      "L" => Large,
      _ => null,
    };
    return size is not null;
  }
}
