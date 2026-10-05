using System.Text.Json;
using Atli.Reports.Blazor.Tailwind.Contracts;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Atli.Reports.Blazor.Tailwind.Build;

public sealed class ResolveTailwindManifests : DiscoveryTask
{
  [Required]
  public string LocalManifestPath { get; set; } = "";

  [Required]
  public string AssemblyPath { get; set; } = "";

  [Required]
  public string ProjectDirectory { get; set; } = "";
  public string ProjectAssetsFile { get; set; } = "";
  public string TargetFramework { get; set; } = "";
  public string RuntimeIdentifier { get; set; } = "";
  public ITaskItem[] References { get; set; } = [];

  [Output]
  public ITaskItem[] Manifests { get; private set; } = [];

  private static StringComparer PathComparer =>
    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

  protected override void ExecuteCore()
  {
    var result = new Dictionary<string, ITaskItem>(PathComparer);
    Add(result, LocalManifestPath, AssemblyPath);
    var implementationPaths = new HashSet<string>(PathComparer);
    foreach (var reference in References)
    {
      var originalPath = reference.GetMetadata("OriginalPath");
      var path = Path.GetFullPath(
        string.IsNullOrEmpty(originalPath) ? reference.ItemSpec : originalPath,
        ProjectDirectory
      );
      if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        continue;
      implementationPaths.Add(path);
      var advertised = reference.GetMetadata("AtliTailwindManifestPath");
      var sidecar = string.IsNullOrWhiteSpace(advertised)
        ? path + ".atli-tailwind.json"
        : advertised;
      if (!string.IsNullOrWhiteSpace(advertised) || File.Exists(sidecar))
        Add(result, sidecar, path);
    }

    if (File.Exists(ProjectAssetsFile))
      ResolvePackages(result, implementationPaths);
    Manifests = result.Values.OrderBy(item => item.ItemSpec, StringComparer.Ordinal).ToArray();
  }

  private void ResolvePackages(
    Dictionary<string, ITaskItem> result,
    HashSet<string> implementations
  )
  {
    using var assets = JsonDocument.Parse(File.ReadAllText(ProjectAssetsFile));
    var document = assets.RootElement;
    var targets = document.GetProperty("targets");
    var targetName = string.IsNullOrEmpty(RuntimeIdentifier)
      ? TargetFramework
      : TargetFramework + "/" + RuntimeIdentifier;
    if (
      !targets.TryGetProperty(targetName, out var selected)
      && !targets.TryGetProperty(TargetFramework, out selected)
    )
      throw new InvalidOperationException(
        $"The restored assets graph has no target '{targetName}'. Restore for the framework and runtime being built."
      );
    var libraries = document.GetProperty("libraries");
    var packageFolders = document
      .GetProperty("packageFolders")
      .EnumerateObject()
      .Select(folder => folder.Name)
      .ToArray();
    foreach (var package in selected.EnumerateObject())
    {
      if (
        !libraries.TryGetProperty(package.Name, out var library)
        || library.GetProperty("type").GetString() != "package"
        || !library.TryGetProperty("path", out var relativePackage)
      )
        continue;
      var packageDirectory = packageFolders
        .Select(folder => Path.Combine(folder, relativePackage.GetString()!))
        .FirstOrDefault(Directory.Exists);
      if (packageDirectory is null)
        continue;
      packageDirectory = Path.GetFullPath(packageDirectory);
      var prefix =
        Path.TrimEndingDirectorySeparator(packageDirectory) + Path.DirectorySeparatorChar;
      var selectedAssets = implementations
        .Where(path =>
          path.StartsWith(
            prefix,
            OperatingSystem.IsWindows()
              ? StringComparison.OrdinalIgnoreCase
              : StringComparison.Ordinal
          )
        )
        .Select(path => Path.GetRelativePath(packageDirectory, path).Replace('\\', '/'))
        .Where(path => !path.StartsWith("ref/", StringComparison.Ordinal))
        .ToHashSet(StringComparer.Ordinal);
      if (selectedAssets.Count == 0)
      {
        // Class-library builds may not copy runtime dependencies. The restore-selected runtime
        // group is still authoritative; never inspect the reference assembly's method bodies.
        if (package.Value.TryGetProperty("runtime", out var runtime))
          foreach (var asset in runtime.EnumerateObject())
            if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
              selectedAssets.Add(asset.Name);
        if (selectedAssets.Count == 0 && package.Value.TryGetProperty("compile", out var compile))
          foreach (var asset in compile.EnumerateObject())
            if (
              asset.Name.StartsWith("lib/", StringComparison.Ordinal)
              && asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            )
              selectedAssets.Add(asset.Name);
      }
      var frameworks = selectedAssets
        .Select(GetAssetFramework)
        .Where(framework => framework is not null)
        .Distinct(StringComparer.Ordinal);
      foreach (var framework in frameworks)
      {
        var indexPath = Path.Combine(
          packageDirectory,
          "atli-tailwind",
          "v1",
          framework!,
          "index.json"
        );
        if (!File.Exists(indexPath))
        {
          var relativeIndex = $"atli-tailwind/v1/{framework}/index.json";
          if (
            library.TryGetProperty("files", out var files)
            && files.EnumerateArray().Any(file => file.GetString() == relativeIndex)
          )
            throw new InvalidOperationException(
              $"Package '{package.Name}' advertises Tailwind manifest index '{relativeIndex}', but that file is missing. Restore the package again."
            );
          continue;
        }
        var index = ManifestIO.Read<ManifestPackageIndex>(indexPath);
        if (index.SchemaVersion != "1.0")
          throw new InvalidOperationException(
            $"Package '{package.Name}' advertises unsupported Tailwind manifest index schema '{index.SchemaVersion}'."
          );
        foreach (var export in index.Exports)
        {
          if (!selectedAssets.Contains(export.AssemblyPath))
            continue;
          Add(
            result,
            ResolvePackagePath(packageDirectory, export.ManifestPath),
            ResolvePackagePath(packageDirectory, export.AssemblyPath)
          );
        }
      }
    }
  }

  private static string? GetAssetFramework(string path)
  {
    var segments = path.Split('/');
    for (var index = 0; index + 2 < segments.Length; index++)
      if (segments[index] == "lib")
        return segments[index + 1];
    return null;
  }

  private static string ResolvePackagePath(string packageDirectory, string relativePath)
  {
    if (
      string.IsNullOrWhiteSpace(relativePath)
      || Path.IsPathRooted(relativePath)
      || relativePath.Replace('\\', '/').Split('/').Any(segment => segment is "" or "." or "..")
    )
      throw new InvalidOperationException(
        $"Invalid Tailwind package export path '{relativePath}'."
      );
    var result = Path.GetFullPath(
      relativePath.Replace('/', Path.DirectorySeparatorChar),
      packageDirectory
    );
    var prefix = Path.TrimEndingDirectorySeparator(packageDirectory) + Path.DirectorySeparatorChar;
    if (
      !result.StartsWith(
        prefix,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
      )
    )
      throw new InvalidOperationException(
        $"Tailwind export '{relativePath}' escapes its package directory."
      );
    return result;
  }

  private static void Add(
    Dictionary<string, ITaskItem> result,
    string manifestPath,
    string implementationPath
  )
  {
    manifestPath = Path.GetFullPath(manifestPath);
    implementationPath = Path.GetFullPath(implementationPath);
    ValidateManifest(manifestPath, implementationPath);
    var item = new TaskItem(manifestPath);
    item.SetMetadata("ImplementationPath", implementationPath);
    result.TryAdd(manifestPath, item);
  }
}
