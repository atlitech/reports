using System.Text.Json;

namespace Atli.Reports.Blazor.Tailwind.Contracts;

public sealed class ReportGraphRequest
{
  public string Bundle { get; set; } = "";
  public string AssemblyName { get; set; } = "";
  public string RootAssembly { get; set; } = "";
  public string RootComponent { get; set; } = "";
  public string ProjectDirectory { get; set; } = "";
  public List<ComponentAddition> Components { get; set; } = [];
  public List<SourceAddition> Sources { get; set; } = [];
  public List<ExternalComponentPolicy> ExternalPolicies { get; set; } = [];
}

public sealed class ReportGraph
{
  public string Bundle { get; set; } = "";
  public string Root { get; set; } = "";
  public List<GraphComponent> Components { get; set; } = [];
  public List<string> CandidateFragments { get; set; } = [];
  public List<string> ExternalComponents { get; set; } = [];
  public List<DiscoveryDiagnostic> Unresolved { get; set; } = [];
  public string Fingerprint { get; set; } = "";
}

public sealed class GraphComponent
{
  public string Identity { get; set; } = "";
  public string Path { get; set; } = "";
}

/// <summary>Resolves portable component manifests without loading application assemblies.</summary>
public sealed class ReportGraphResolver
{
  private readonly Dictionary<string, ComponentManifest> manifests = new(StringComparer.Ordinal);
  private readonly Dictionary<string, ComponentEntry> components = new(StringComparer.Ordinal);

  public ReportGraphResolver(IEnumerable<ComponentManifest> inputs)
  {
    foreach (var manifest in inputs)
    {
      ManifestIO.Validate(manifest, manifest.Artifact.AssemblyName);
      var assembly = manifest.Artifact.AssemblyName;
      if (manifests.TryGetValue(assembly, out var existing))
      {
        if (
          JsonSerializer.Serialize(existing, ManifestIO.JsonOptions)
          != JsonSerializer.Serialize(manifest, ManifestIO.JsonOptions)
        )
          throw new InvalidOperationException(
            $"Ambiguous Tailwind manifests for assembly '{assembly}'. Resolve one implementation and manifest for the selected target framework."
          );
        continue;
      }
      manifests.Add(assembly, manifest);
      foreach (var component in manifest.Components)
        components.Add(Key(assembly, component.TypeName), component);
    }
  }

  public ReportGraph Resolve(ReportGraphRequest request)
  {
    var rootAssembly = string.IsNullOrWhiteSpace(request.RootAssembly)
      ? request.AssemblyName
      : request.RootAssembly;
    var root = Key(rootAssembly, request.RootComponent);
    if (!components.ContainsKey(root))
      throw new InvalidOperationException(
        $"Tailwind bundle '{request.Bundle}' root '{root}' has no usable component manifest. Add the Discovery package to its project, correct RootComponent/RootAssembly, or remove RootComponent and use explicit @source entries."
      );
    var graph = new ReportGraph { Bundle = request.Bundle, Root = root };
    var queue = new Queue<(string Assembly, string Type, string Path, bool Declared)>();
    queue.Enqueue((rootAssembly, request.RootComponent, root, true));
    var additions = request.Components.Where(item => item.Bundle == request.Bundle).ToList();
    foreach (var addition in additions)
    {
      ManifestDeclarations.ValidateScope(
        addition.OwnerComponent,
        addition.Bundle,
        addition.TypeName
      );
      var assembly = string.IsNullOrWhiteSpace(addition.AssemblyName)
        ? request.AssemblyName
        : addition.AssemblyName;
      var identity = Key(assembly, addition.TypeName);
      if (!components.ContainsKey(identity))
        throw new InvalidOperationException(
          $"Declared Tailwind component '{identity}' for bundle '{request.Bundle}' has no usable manifest entry. Correct the type or enable manifest production in its project."
        );
      queue.Enqueue((assembly, addition.TypeName, root + " -> declared " + identity, true));
    }
    var visited = new HashSet<string>(StringComparer.Ordinal);
    var fragments = new HashSet<string>(StringComparer.Ordinal);
    while (queue.TryDequeue(out var next))
    {
      var identity = Key(next.Assembly, next.Type);
      if (next.Declared && !components.ContainsKey(identity))
        throw new InvalidOperationException(
          $"Declared Tailwind component '{identity}' has no usable manifest entry ({next.Path}). Correct the declaration or enable manifest production in its project."
        );
      if (!visited.Add(identity))
        continue;
      if (identity != root && IsExternal(request, next.Assembly, next.Type))
      {
        graph.ExternalComponents.Add(next.Path);
        continue;
      }
      if (!components.TryGetValue(identity, out var entry))
      {
        if (manifests.ContainsKey(next.Assembly))
          throw new InvalidOperationException(
            $"Tailwind manifest for '{next.Assembly}' is missing referenced component '{next.Type}' (bundle '{request.Bundle}', path: {next.Path}). Rebuild the matching producer package."
          );
        graph.Unresolved.Add(
          new DiscoveryDiagnostic
          {
            Code = "ATLI1002",
            Kind = "missing-manifest",
            Message =
              $"Bundle '{request.Bundle}' cannot discover '{identity}' ({next.Path}). Add the Discovery package to that library or declare AtliTailwindExternal Policy=SelfStyled and supply its CSS explicitly.",
          }
        );
        continue;
      }
      graph.Components.Add(new GraphComponent { Identity = identity, Path = next.Path });
      foreach (var fragment in entry.CandidateFragments)
        fragments.Add(fragment);
      foreach (var unresolved in entry.Unresolved)
      {
        if (
          (entry.HasDeclaredAlternatives || additions.Count > 0)
          && unresolved.Kind is "dynamic-component" or "generic-component" or "external-fragment"
        )
          continue;
        graph.Unresolved.Add(
          new DiscoveryDiagnostic
          {
            Code = unresolved.Code,
            Kind = unresolved.Kind,
            Location = unresolved.Location,
            Message = $"Bundle '{request.Bundle}', {next.Path}: {unresolved.Message}",
          }
        );
      }
      foreach (
        var dependency in entry.Dependencies.OrderBy(
          item => item.ToString(),
          StringComparer.Ordinal
        )
      )
        queue.Enqueue(
          (
            dependency.AssemblyName,
            dependency.TypeName,
            next.Path + " -> " + dependency,
            dependency.IsDeclared
          )
        );
    }
    foreach (var source in request.Sources.Where(item => item.Bundle == request.Bundle))
    {
      ManifestDeclarations.ValidateScope(source.OwnerComponent, source.Bundle, source.Path);
      var path = System.IO.Path.GetFullPath(source.Path, request.ProjectDirectory);
      if (!File.Exists(path))
        throw new InvalidOperationException(
          $"Tailwind candidate source '{path}' for bundle '{request.Bundle}' is missing."
        );
      fragments.Add(File.ReadAllText(path));
    }
    graph.Components = graph
      .Components.OrderBy(item => item.Identity, StringComparer.Ordinal)
      .ToList();
    graph.CandidateFragments = fragments.Order(StringComparer.Ordinal).ToList();
    graph.ExternalComponents.Sort(StringComparer.Ordinal);
    graph.Unresolved = graph
      .Unresolved.OrderBy(item => item.Message, StringComparer.Ordinal)
      .ToList();
    graph.Fingerprint = ManifestIO.HashText(
      JsonSerializer.Serialize(graph, ManifestIO.JsonOptions)
    );
    return graph;
  }

  private static string Key(string assembly, string type) => $"{assembly}:{type}";

  private static bool IsExternal(ReportGraphRequest request, string assembly, string type)
  {
    if (
      assembly
      is "Microsoft.AspNetCore.Components"
        or "Microsoft.AspNetCore.Components.Web"
        or "Microsoft.AspNetCore.Components.Forms"
    )
      return true;
    return request.ExternalPolicies.Any(policy =>
      policy.AssemblyName == assembly
      && (policy.TypeName.Length == 0 || policy.TypeName == type)
      && (policy.Bundle.Length == 0 || policy.Bundle == request.Bundle)
      && policy.Policy == "SelfStyled"
    );
  }
}
