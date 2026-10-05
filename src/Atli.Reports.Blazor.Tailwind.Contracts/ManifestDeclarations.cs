namespace Atli.Reports.Blazor.Tailwind.Contracts;

public static class ManifestDeclarations
{
  public static void ApplyOwned(
    ComponentManifest manifest,
    IEnumerable<ComponentAddition> components,
    IEnumerable<SourceAddition> sources,
    string projectDirectory
  )
  {
    foreach (var addition in components)
    {
      ValidateScope(addition.OwnerComponent, addition.Bundle, addition.TypeName);
      if (addition.OwnerComponent.Length == 0)
        continue;
      var owner = FindOwner(manifest, addition.OwnerComponent);
      var assembly = string.IsNullOrWhiteSpace(addition.AssemblyName)
        ? manifest.Artifact.AssemblyName
        : addition.AssemblyName;
      if (string.IsNullOrWhiteSpace(addition.TypeName))
        throw new InvalidOperationException(
          "AtliTailwindComponent requires a component type name."
        );
      if (
        assembly == manifest.Artifact.AssemblyName
        && !manifest.Components.Any(component => component.TypeName == addition.TypeName)
      )
        throw new InvalidOperationException(
          $"Declared component '{addition.TypeName}' does not exist in '{assembly}'."
        );
      var dependency = owner.Dependencies.SingleOrDefault(dependency =>
        dependency.AssemblyName == assembly && dependency.TypeName == addition.TypeName
      );
      if (dependency is null)
        owner.Dependencies.Add(
          new ComponentReference
          {
            AssemblyName = assembly,
            TypeName = addition.TypeName,
            IsDeclared = true,
          }
        );
      else
        dependency.IsDeclared = true;
      owner.HasDeclaredAlternatives = true;
    }
    foreach (var addition in sources)
    {
      ValidateScope(addition.OwnerComponent, addition.Bundle, addition.Path);
      if (addition.OwnerComponent.Length == 0)
        continue;
      var owner = FindOwner(manifest, addition.OwnerComponent);
      var source = System.IO.Path.GetFullPath(addition.Path, projectDirectory);
      if (!File.Exists(source))
        throw new InvalidOperationException(
          $"Candidate source '{source}' for '{addition.OwnerComponent}' does not exist."
        );
      owner.CandidateFragments.Add(File.ReadAllText(source));
    }
    foreach (var component in manifest.Components)
    {
      component.Dependencies = component
        .Dependencies.OrderBy(dependency => dependency.ToString(), StringComparer.Ordinal)
        .ToList();
      component.CandidateFragments = component
        .CandidateFragments.Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();
    }
  }

  public static void ValidateScope(string owner, string bundle, string item)
  {
    if (string.IsNullOrWhiteSpace(owner) == string.IsNullOrWhiteSpace(bundle))
      throw new InvalidOperationException(
        $"Tailwind addition '{item}' must specify exactly one OwnerComponent or Bundle scope."
      );
  }

  private static ComponentEntry FindOwner(ComponentManifest manifest, string owner) =>
    manifest.Components.SingleOrDefault(component => component.TypeName == owner)
    ?? throw new InvalidOperationException(
      $"OwnerComponent '{owner}' is not a component in '{manifest.Artifact.AssemblyName}'. Use Bundle scope for application overrides of referenced libraries."
    );
}
