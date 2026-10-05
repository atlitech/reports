using System.Text.Json;
using Atli.Reports.Blazor.Tailwind.Contracts;
using Microsoft.Build.Framework;

namespace Atli.Reports.Blazor.Tailwind.Build;

public sealed partial class BuildTailwindCss
{
  public ITaskItem[] ManifestFiles { get; set; } = [];
  public ITaskItem[] Components { get; set; } = [];
  public ITaskItem[] Sources { get; set; } = [];
  public ITaskItem[] ExternalPolicies { get; set; } = [];
  public string AssemblyName { get; set; } = "";
  public string AssemblyPath { get; set; } = "";
  public string ProjectAssetsFile { get; set; } = "";
  public bool Strict { get; set; }

  private const string InventoryFile = "graph-success.json";
  private const string CacheFile = "graph-cache.json";

  private List<ComponentAddition> GetComponentAdditions() =>
    Components
      .Select(item => new ComponentAddition
      {
        TypeName = item.ItemSpec,
        AssemblyName = item.GetMetadata("Assembly"),
        OwnerComponent = item.GetMetadata("OwnerComponent"),
        Bundle = item.GetMetadata("Bundle"),
      })
      .ToList();

  private List<SourceAddition> GetSourceAdditions() =>
    Sources
      .Select(item => new SourceAddition
      {
        Path = item.ItemSpec,
        OwnerComponent = item.GetMetadata("OwnerComponent"),
        Bundle = item.GetMetadata("Bundle"),
      })
      .ToList();

  private List<ExternalComponentPolicy> GetExternalPolicies() =>
    ExternalPolicies
      .Select(item => new ExternalComponentPolicy
      {
        AssemblyName = item.ItemSpec,
        TypeName = item.GetMetadata("Component"),
        Policy = item.GetMetadata("Policy"),
        Bundle = item.GetMetadata("Bundle"),
      })
      .ToList();

  private void ValidateDiscoveryDeclarations(Dictionary<string, BundleInput> bundles)
  {
    foreach (var addition in GetComponentAdditions())
    {
      ManifestDeclarations.ValidateScope(
        addition.OwnerComponent,
        addition.Bundle,
        addition.TypeName
      );
      ValidateBundleScope(addition.Bundle, bundles);
    }
    foreach (var addition in GetSourceAdditions())
    {
      ManifestDeclarations.ValidateScope(addition.OwnerComponent, addition.Bundle, addition.Path);
      ValidateBundleScope(addition.Bundle, bundles);
    }
    foreach (var policy in GetExternalPolicies())
    {
      if (string.IsNullOrWhiteSpace(policy.AssemblyName) || policy.Policy != "SelfStyled")
        throw new InvalidOperationException(
          "AtliTailwindExternal requires an assembly Include and Policy=SelfStyled. Supply external styles explicitly in your report CSS."
        );
      ValidateBundleScope(policy.Bundle, bundles);
    }
    foreach (var bundle in bundles.Values)
      if (
        bundle.Declaration.GetMetadata("RootComponent").Length == 0
        && bundle.Declaration.GetMetadata("RootAssembly").Length > 0
      )
        throw new InvalidOperationException(
          $"Tailwind bundle '{bundle.Name}' specifies RootAssembly without RootComponent."
        );
  }

  private static void ValidateBundleScope(string name, Dictionary<string, BundleInput> bundles)
  {
    if (name.Length == 0)
      return;
    if (
      !bundles.TryGetValue(name, out var bundle)
      || bundle.Name != name
      || bundle.Declaration.GetMetadata("RootComponent").Length == 0
    )
      throw new InvalidOperationException(
        $"Tailwind addition references unknown graph-mode Bundle '{name}'. Use the exact BundlePath (or input path without .tailwind.css)."
      );
  }

  private DiscoveryBuild PrepareDiscovery(
    string intermediate,
    Dictionary<string, BundleInput> bundles
  )
  {
    var inventoryPath = Path.Combine(intermediate, InventoryFile);
    var cachePath = Path.Combine(intermediate, CacheFile);
    GraphInventory? previous = null;
    if (File.Exists(cachePath))
    {
      try
      {
        previous = ManifestIO.Read<GraphInventory>(cachePath);
      }
      catch (InvalidOperationException)
      { /* A real build can replace an invalid previous cache. */
      }
    }
    // Publish must not accept partially updated CSS if any later graph or compiler step fails.
    if (File.Exists(inventoryPath))
      File.Delete(inventoryPath);
    var build = new DiscoveryBuild { Previous = previous };
    var graphInputs = bundles
      .Values.Where(bundle => bundle.Declaration.GetMetadata("RootComponent").Length > 0)
      .ToList();
    if (graphInputs.Count == 0)
      return build;
    if (string.IsNullOrWhiteSpace(AssemblyName) || string.IsNullOrWhiteSpace(AssemblyPath))
      throw new InvalidOperationException(
        "Automatic Tailwind discovery requires the current AssemblyName and implementation AssemblyPath after compilation."
      );
    var assembly = Path.GetFullPath(AssemblyPath, ProjectDirectory);
    build.Inventory.AssemblyPath = assembly;
    build.Inventory.AssemblyHash = ManifestIO.HashFile(assembly);
    build.Inventory.ToolFingerprint = BuildToolFingerprint();
    build.Inventory.AssetsHash = AssetsFingerprint();
    var manifests = new List<ComponentManifest>();
    foreach (var item in ManifestFiles)
    {
      var path = Path.GetFullPath(item.ItemSpec, ProjectDirectory);
      var manifest = ManifestIO.Read<ComponentManifest>(path);
      ManifestIO.Validate(manifest, path);
      var implementation = item.GetMetadata("ImplementationPath");
      if (string.IsNullOrWhiteSpace(implementation))
        throw new InvalidOperationException(
          $"Manifest '{path}' has no selected ImplementationPath; cannot validate its provenance."
        );
      implementation = Path.GetFullPath(implementation, ProjectDirectory);
      if (
        !File.Exists(implementation)
        || !ManifestIO
          .HashFile(implementation)
          .Equals(manifest.Artifact.ImplementationSha256, StringComparison.OrdinalIgnoreCase)
      )
        throw new InvalidOperationException(
          $"Manifest '{path}' does not match implementation '{implementation}'. Rebuild or restore the matching component library."
        );
      build.Inventory.Artifacts.Add(
        new GraphArtifact
        {
          ManifestPath = path,
          ManifestHash = ManifestIO.HashFile(path),
          AssemblyPath = implementation,
          AssemblyHash = manifest.Artifact.ImplementationSha256,
        }
      );
      manifests.Add(manifest);
    }
    var resolver = new ReportGraphResolver(manifests);
    var componentAdditions = GetComponentAdditions();
    var sourceAdditions = GetSourceAdditions();
    var externalPolicies = GetExternalPolicies();
    foreach (var source in sourceAdditions)
    {
      var sourcePath = Path.GetFullPath(source.Path, ProjectDirectory);
      build.Inventory.SourceFiles.Add(
        new GraphSourceFile { Path = sourcePath, Hash = ManifestIO.HashFile(sourcePath) }
      );
    }
    foreach (var bundle in graphInputs)
    {
      var graph = resolver.Resolve(
        new ReportGraphRequest
        {
          Bundle = bundle.Name,
          AssemblyName = AssemblyName,
          RootComponent = bundle.Declaration.GetMetadata("RootComponent"),
          RootAssembly = bundle.Declaration.GetMetadata("RootAssembly"),
          ProjectDirectory = ProjectDirectory,
          Components = componentAdditions,
          Sources = sourceAdditions,
          ExternalPolicies = externalPolicies,
        }
      );
      ReportCoverage(graph.Unresolved);
      var basePath = Path.Combine(intermediate, "graph", bundle.Name);
      var candidatePath = basePath + ".candidates.txt";
      var wrapperPath = basePath + ".input.css";
      ManifestIO.WriteText(candidatePath, string.Join('\n', graph.CandidateFragments) + "\n");
      // Importing the original stylesheet preserves its own @source/@import base directory.
      ManifestIO.WriteText(
        wrapperPath,
        $"@import \"{CssPath(bundle.Input)}\";\n@source \"{CssPath(candidatePath)}\";\n"
      );
      var inputText = File.ReadAllText(bundle.Input);
      var compilerFingerprint = CompilerFingerprint();
      var fingerprint = ManifestIO.HashText(
        graph.Fingerprint + "\n" + inputText + "\n" + compilerFingerprint
      );
      // This exact supported input has no unknown CSS/source/plugin dependency. More complex
      // stylesheets remain conservative until their full dependency tracking is proven.
      var canSkip =
        inputText.Trim()
        is "@import \"tailwindcss\" source(none);"
          or "@import 'tailwindcss' source(none);";
      var previousBundle = previous?.Bundles.SingleOrDefault(item => item.Name == bundle.Name);
      var unchanged =
        canSkip
        && previousBundle?.Fingerprint == fingerprint
        && File.Exists(bundle.Output.ItemSpec)
        && ManifestIO.HashFile(bundle.Output.ItemSpec) == previousBundle.OutputHash;
      var result = new GraphBundle
      {
        Name = bundle.Name,
        RootComponent = bundle.Declaration.GetMetadata("RootComponent"),
        RootAssembly = bundle.Declaration.GetMetadata("RootAssembly"),
        InputHash = ManifestIO.HashFile(bundle.Input),
        OutputPath = bundle.Output.ItemSpec,
        Fingerprint = fingerprint,
        DeclarationHash = DeclarationFingerprint(bundle),
        Unresolved = graph.Unresolved,
      };
      build.Inventory.Bundles.Add(result);
      build.Bundles.Add(bundle.Name, new PreparedGraphBundle(wrapperPath, unchanged));
      ManifestIO.Write(
        basePath + ".explain.json",
        new
        {
          graph.Bundle,
          graph.Root,
          graph.Components,
          graph.ExternalComponents,
          graph.Unresolved,
          graph.Fingerprint,
          CandidateCount = graph.CandidateFragments.Count,
          RebuildReason = unchanged ? "unchanged"
          : canSkip ? "component, input, compiler or output changed"
          : "conservative CSS dependency tracking",
          SourcePolicy = "Use @import \"tailwindcss\" source(none) to disable Tailwind project-wide scanning. Explicit @source inputs can broaden this bundle.",
          ManifestAssemblies = manifests.Select(item => new
          {
            item.Artifact.AssemblyName,
            item.SchemaVersion,
            item.ProducerVersion,
          }),
        }
      );
    }
    return build;
  }

  private string CompilerFingerprint()
  {
    var executableHash = string.IsNullOrWhiteSpace(Executable)
      ? "official"
      : ManifestIO.HashFile(Path.GetFullPath(Executable, ProjectDirectory));
    return $"{BuildToolFingerprint()}\n{executableHash}";
  }

  private string BuildToolFingerprint() =>
    ManifestIO.HashText(
      $"{Version}\n{Minify}\n{ManifestIO.HashFile(typeof(BuildTailwindCss).Assembly.Location)}\n{ManifestIO.HashFile(typeof(ReportGraphResolver).Assembly.Location)}"
    );

  private string AssetsFingerprint()
  {
    if (string.IsNullOrWhiteSpace(ProjectAssetsFile))
      throw new InvalidOperationException(
        "Automatic Tailwind discovery requires ProjectAssetsFile to validate the selected dependency graph."
      );
    return ManifestIO.HashFile(Path.GetFullPath(ProjectAssetsFile, ProjectDirectory));
  }

  private string DeclarationFingerprint(BundleInput bundle) =>
    ManifestIO.HashText(
      JsonSerializer.Serialize(
        new
        {
          Root = bundle.Declaration.GetMetadata("RootComponent"),
          Assembly = bundle.Declaration.GetMetadata("RootAssembly"),
          Components = GetComponentAdditions(),
          Sources = GetSourceAdditions(),
          Policies = GetExternalPolicies(),
        },
        ManifestIO.JsonOptions
      )
    );

  private void ReportCoverage(IEnumerable<DiscoveryDiagnostic> diagnostics)
  {
    foreach (var diagnostic in diagnostics)
    {
      if (Strict)
        throw new InvalidOperationException(
          $"{diagnostic.Code}: {diagnostic.Message} AtliTailwindStrict requires declared coverage."
        );
      Log.LogWarning(
        null,
        diagnostic.Code,
        null,
        diagnostic.Location,
        0,
        0,
        0,
        0,
        diagnostic.Message
      );
    }
  }

  private static string CssPath(string path) =>
    path.Replace('\\', '/')
      .Replace("\"", "\\\"", StringComparison.Ordinal)
      .Replace("\r", "\\d ", StringComparison.Ordinal)
      .Replace("\n", "\\a ", StringComparison.Ordinal);

  private static void CompleteDiscovery(string intermediate, DiscoveryBuild build)
  {
    foreach (var bundle in build.Inventory.Bundles)
      bundle.OutputHash = ManifestIO.HashFile(bundle.OutputPath);
    foreach (var removed in build.Previous?.Bundles ?? [])
      if (!build.Inventory.Bundles.Any(current => current.Name == removed.Name))
      {
        var basePath = Path.Combine(intermediate, "graph", ValidateBundlePath(removed.Name));
        foreach (var extension in new[] { ".input.css", ".candidates.txt", ".explain.json" })
          if (File.Exists(basePath + extension))
            File.Delete(basePath + extension);
      }
    if (build.Inventory.Bundles.Count > 0)
    {
      ManifestIO.Write(Path.Combine(intermediate, CacheFile), build.Inventory);
      ManifestIO.Write(Path.Combine(intermediate, InventoryFile), build.Inventory);
    }
    else if (File.Exists(Path.Combine(intermediate, CacheFile)))
      File.Delete(Path.Combine(intermediate, CacheFile));
  }

  private void ValidateDiscoveryPublish(
    string intermediate,
    Dictionary<string, BundleInput> bundles
  )
  {
    var graphInputs = bundles
      .Values.Where(bundle => bundle.Declaration.GetMetadata("RootComponent").Length > 0)
      .ToList();
    if (graphInputs.Count == 0)
      return;
    var path = Path.Combine(intermediate, InventoryFile);
    const string remedy =
      "Run a successful dotnet build with the same configuration before publishing with --no-build.";
    if (!File.Exists(path))
      throw new InvalidOperationException(
        $"A successful Tailwind graph build inventory is missing. {remedy}"
      );
    var inventory = ManifestIO.Read<GraphInventory>(path);
    if (inventory.ToolFingerprint != BuildToolFingerprint())
      throw new InvalidOperationException(
        $"Tailwind compiler settings or build tooling changed after the successful graph build. {remedy}"
      );
    if (inventory.AssetsHash != AssetsFingerprint())
      throw new InvalidOperationException(
        $"The restored dependency graph changed after the successful Tailwind build. {remedy}"
      );
    foreach (var source in inventory.SourceFiles)
      if (!File.Exists(source.Path) || ManifestIO.HashFile(source.Path) != source.Hash)
        throw new InvalidOperationException(
          $"Tailwind candidate source '{source.Path}' changed after the successful build. {remedy}"
        );
    var assembly = Path.GetFullPath(AssemblyPath, ProjectDirectory);
    if (!File.Exists(assembly) || ManifestIO.HashFile(assembly) != inventory.AssemblyHash)
      throw new InvalidOperationException(
        $"The compiled assembly no longer matches its Tailwind graph CSS. {remedy}"
      );
    foreach (var artifact in inventory.Artifacts)
      if (
        !File.Exists(artifact.AssemblyPath)
        || ManifestIO.HashFile(artifact.AssemblyPath) != artifact.AssemblyHash
        || !File.Exists(artifact.ManifestPath)
        || ManifestIO.HashFile(artifact.ManifestPath) != artifact.ManifestHash
      )
        throw new InvalidOperationException(
          $"Tailwind producer artifact '{artifact.ManifestPath}' changed after the successful graph build. {remedy}"
        );
    if (inventory.Bundles.Count != graphInputs.Count)
      throw new InvalidOperationException($"Tailwind graph bundle declarations changed. {remedy}");
    foreach (var bundle in graphInputs)
    {
      var saved = inventory.Bundles.SingleOrDefault(item => item.Name == bundle.Name);
      if (
        saved is null
        || saved.InputHash != ManifestIO.HashFile(bundle.Input)
        || saved.OutputHash != ManifestIO.HashFile(bundle.Output.ItemSpec)
        || saved.DeclarationHash != DeclarationFingerprint(bundle)
      )
        throw new InvalidOperationException(
          $"Tailwind graph bundle '{bundle.Name}' changed after its successful build. {remedy}"
        );
      ReportCoverage(saved.Unresolved);
    }
  }

  private sealed class DiscoveryBuild
  {
    public GraphInventory? Previous { get; init; }
    public GraphInventory Inventory { get; } = new();
    public Dictionary<string, PreparedGraphBundle> Bundles { get; } = new(StringComparer.Ordinal);
  }

  private sealed record PreparedGraphBundle(string CompilerInput, bool SkipCompilation);

  private sealed class GraphInventory
  {
    public string AssemblyPath { get; set; } = "";
    public string AssemblyHash { get; set; } = "";
    public string ToolFingerprint { get; set; } = "";
    public string AssetsHash { get; set; } = "";
    public List<GraphArtifact> Artifacts { get; set; } = [];
    public List<GraphSourceFile> SourceFiles { get; set; } = [];
    public List<GraphBundle> Bundles { get; set; } = [];
  }

  private sealed class GraphArtifact
  {
    public string ManifestPath { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public string AssemblyHash { get; set; } = "";
  }

  private sealed class GraphSourceFile
  {
    public string Path { get; set; } = "";
    public string Hash { get; set; } = "";
  }

  private sealed class GraphBundle
  {
    public string Name { get; set; } = "";
    public string RootComponent { get; set; } = "";
    public string RootAssembly { get; set; } = "";
    public string InputHash { get; set; } = "";
    public string DeclarationHash { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string OutputHash { get; set; } = "";
    public List<DiscoveryDiagnostic> Unresolved { get; set; } = [];
  }
}
