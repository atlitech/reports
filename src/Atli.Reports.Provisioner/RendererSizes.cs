using System.Diagnostics.CodeAnalysis;
using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Provisioner;

/// <summary>Renderer sizes by name, for flags, settings, and sandbox labels.</summary>
internal static class RendererSizes
{
  private static readonly RendererSize[] All =
  [
    RendererSize.Small,
    RendererSize.Medium,
    RendererSize.Large,
  ];

  /// <summary>Finds the size named <paramref name="name"/> (case-insensitive) without throwing.</summary>
  public static bool TryParse(string? name, [NotNullWhen(true)] out RendererSize? size)
  {
    size = All.FirstOrDefault(candidate =>
      string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
    );
    return size is not null;
  }
}
