namespace Atli.Reports.Blazor.Tailwind.Contracts;

public sealed class CompilerSnapshot
{
  public string ProjectDirectory { get; set; } = "";
  public string[] Arguments { get; set; } = [];
}

public sealed class DiscoveryRequest
{
  public string ProjectDirectory { get; set; } = "";
  public string AssemblyPath { get; set; } = "";
  public string CompilerArgumentsPath { get; set; } = "";
  public string TargetFramework { get; set; } = "";
  public string RuntimeIdentifier { get; set; } = "";
  public string OutputPath { get; set; } = "";
}

public sealed class ComponentManifest
{
  public const string CurrentSchemaVersion = "1.0";
  public string SchemaVersion { get; set; } = CurrentSchemaVersion;
  public string ProducerVersion { get; set; } = "1.0.0";
  public string[] RequiredCapabilities { get; set; } =
  ["component-graph-v1", "candidate-fragments-v1"];
  public ManifestArtifact Artifact { get; set; } = new();
  public List<ComponentEntry> Components { get; set; } = [];
}

public sealed class ManifestArtifact
{
  public string AssemblyName { get; set; } = "";
  public string AssemblyVersion { get; set; } = "";
  public string PublicKeyToken { get; set; } = "";
  public string TargetFramework { get; set; } = "";
  public string RuntimeIdentifier { get; set; } = "";
  public string ImplementationSha256 { get; set; } = "";
}

public sealed class ComponentEntry
{
  public string TypeName { get; set; } = "";
  public List<ComponentReference> Dependencies { get; set; } = [];
  public List<string> CandidateFragments { get; set; } = [];
  public List<DiscoveryDiagnostic> Unresolved { get; set; } = [];
  public bool HasDeclaredAlternatives { get; set; }
}

public sealed class ComponentReference
{
  public string AssemblyName { get; set; } = "";
  public string TypeName { get; set; } = "";
  public bool IsDeclared { get; set; }

  public override string ToString() => $"{AssemblyName}:{TypeName}";
}

public sealed class DiscoveryDiagnostic
{
  public string Code { get; set; } = "ATLI1001";
  public string Kind { get; set; } = "";
  public string Message { get; set; } = "";
  public string Location { get; set; } = "";
}

public sealed class ComponentAddition
{
  public string AssemblyName { get; set; } = "";
  public string TypeName { get; set; } = "";
  public string OwnerComponent { get; set; } = "";
  public string Bundle { get; set; } = "";
}

public sealed class SourceAddition
{
  public string Path { get; set; } = "";
  public string OwnerComponent { get; set; } = "";
  public string Bundle { get; set; } = "";
}

public sealed class ExternalComponentPolicy
{
  public string AssemblyName { get; set; } = "";
  public string TypeName { get; set; } = "";
  public string Policy { get; set; } = "SelfStyled";
  public string Bundle { get; set; } = "";
}
