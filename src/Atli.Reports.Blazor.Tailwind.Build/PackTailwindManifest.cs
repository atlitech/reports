using Atli.Reports.Blazor.Tailwind.Contracts;
using Microsoft.Build.Framework;

namespace Atli.Reports.Blazor.Tailwind.Build;

public sealed class PackTailwindManifest : DiscoveryTask
{
  [Required]
  public string ManifestPath { get; set; } = "";

  [Required]
  public string AssemblyPath { get; set; } = "";

  [Required]
  public string TargetFramework { get; set; } = "";

  [Required]
  public string PackageFramework { get; set; } = "";

  [Required]
  public string IndexPath { get; set; } = "";
  public bool Strict { get; set; }

  protected override void ExecuteCore()
  {
    var manifest = ValidateManifest(ManifestPath, AssemblyPath);
    if (manifest.Artifact.TargetFramework != TargetFramework)
      throw new InvalidOperationException(
        $"Manifest '{ManifestPath}' targets {manifest.Artifact.TargetFramework}, not {TargetFramework}. Rebuild with the configuration being packed."
      );
    if (
      Strict
      && manifest.Components.Any(component =>
        component.Unresolved.Any(diagnostic =>
          !component.HasDeclaredAlternatives
          || diagnostic.Kind
            is not ("dynamic-component" or "generic-component" or "external-fragment")
        )
      )
    )
      throw new InvalidOperationException(
        "The component manifest contains unresolved discovery coverage. Resolve the producer diagnostics before packing with AtliTailwindStrict=true."
      );
    ManifestIO.Write(
      IndexPath,
      new ManifestPackageIndex
      {
        Exports =
        [
          new ManifestPackageExport
          {
            AssemblyPath = $"lib/{PackageFramework}/{Path.GetFileName(AssemblyPath)}",
            ManifestPath = $"atli-tailwind/v1/{PackageFramework}/manifest.json",
          },
        ],
      }
    );
  }
}

internal sealed class ManifestPackageIndex
{
  public string SchemaVersion { get; set; } = "1.0";
  public ManifestPackageExport[] Exports { get; set; } = [];
}

internal sealed class ManifestPackageExport
{
  public string AssemblyPath { get; set; } = "";
  public string ManifestPath { get; set; } = "";
}
