using System.Diagnostics;
using System.Text.Json;
using Atli.Reports.Blazor.Tailwind.Contracts;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Atli.Reports.Blazor.Tailwind.Build;

public sealed class GenerateTailwindManifest : DiscoveryTask, ICancelableTask, IDisposable
{
  private readonly CancellationTokenSource _cancellation = new();

  [Required]
  public string ProjectDirectory { get; set; } = "";

  [Required]
  public string AssemblyPath { get; set; } = "";

  [Required]
  public string CompilerArgumentsPath { get; set; } = "";

  [Required]
  public string AnalysisAssembly { get; set; } = "";

  [Required]
  public string OutputPath { get; set; } = "";
  public string TargetFramework { get; set; } = "";
  public string RuntimeIdentifier { get; set; } = "";
  public string DotnetHost { get; set; } = "dotnet";
  public int TimeoutSeconds { get; set; } = 120;
  public bool HasReportBundles { get; set; }
  public bool ValidateOnly { get; set; }
  public ITaskItem[] Components { get; set; } = [];
  public ITaskItem[] Sources { get; set; } = [];

  [Output]
  public ITaskItem[] GeneratedFiles { get; private set; } = [];

  public void Cancel() => _cancellation.Cancel();

  public void Dispose() => _cancellation.Dispose();

  protected override void ExecuteCore() => ExecuteAsync().GetAwaiter().GetResult();

  private async System.Threading.Tasks.Task ExecuteAsync()
  {
    if (!File.Exists(CompilerArgumentsPath))
      throw new InvalidOperationException(
        $"Compiler snapshot '{CompilerArgumentsPath}' is missing. Run dotnet build so the real C# compilation can capture its arguments."
      );
    if (!File.Exists(AnalysisAssembly))
      throw new InvalidOperationException(
        $"Discovery tool '{AnalysisAssembly}' is missing. Restore the Tailwind package and rebuild."
      );
    if (TimeoutSeconds <= 0)
      throw new InvalidOperationException(
        "AtliTailwindDiscoveryTimeoutSeconds must be greater than zero."
      );

    foreach (var item in Components.Concat(Sources))
    {
      ManifestDeclarations.ValidateScope(
        item.GetMetadata("OwnerComponent"),
        item.GetMetadata("Bundle"),
        item.ItemSpec
      );
      if (!HasReportBundles && !string.IsNullOrWhiteSpace(item.GetMetadata("Bundle")))
        throw new InvalidOperationException(
          $"Tailwind addition '{item.ItemSpec}' uses Bundle scope in a producer without report bundles. Use OwnerComponent for library declarations."
        );
    }
    var componentAdditions = Components
      .Where(item => !string.IsNullOrWhiteSpace(item.GetMetadata("OwnerComponent")))
      .Select(item => new ComponentAddition
      {
        TypeName = item.ItemSpec,
        AssemblyName = item.GetMetadata("Assembly"),
        OwnerComponent = item.GetMetadata("OwnerComponent"),
        Bundle = item.GetMetadata("Bundle"),
      })
      .ToArray();
    var sourceAdditions = Sources
      .Where(item => !string.IsNullOrWhiteSpace(item.GetMetadata("OwnerComponent")))
      .Select(item => new SourceAddition
      {
        Path = Path.GetFullPath(item.ItemSpec, ProjectDirectory),
        OwnerComponent = item.GetMetadata("OwnerComponent"),
        Bundle = item.GetMetadata("Bundle"),
      })
      .ToArray();
    var fingerprint = ManifestIO.HashText(
      JsonSerializer.Serialize(
        new
        {
          assembly = ManifestIO.HashFile(AssemblyPath),
          symbols = File.Exists(Path.ChangeExtension(AssemblyPath, ".pdb"))
            ? ManifestIO.HashFile(Path.ChangeExtension(AssemblyPath, ".pdb"))
            : "embedded",
          arguments = ManifestIO.HashFile(CompilerArgumentsPath),
          manifestTask = ManifestIO.HashFile(typeof(GenerateTailwindManifest).Assembly.Location),
          tools = Directory
            .GetFiles(Path.GetDirectoryName(AnalysisAssembly)!, "*.dll")
            .Order(StringComparer.Ordinal)
            .Select(ManifestIO.HashFile)
            .ToArray(),
          targetFramework = TargetFramework,
          runtimeIdentifier = RuntimeIdentifier,
          componentAdditions,
          sourceAdditions = sourceAdditions.Select(item => new
          {
            item.Path,
            item.OwnerComponent,
            item.Bundle,
            hash = ManifestIO.HashFile(item.Path),
          }),
        },
        ManifestIO.JsonOptions
      )
    );
    var statePath = OutputPath + ".state";
    if (
      File.Exists(OutputPath)
      && File.Exists(statePath)
      && File.ReadAllText(statePath) == fingerprint
    )
    {
      ValidateManifest(OutputPath, AssemblyPath);
      GeneratedFiles = [new TaskItem(OutputPath), new TaskItem(statePath)];
      return;
    }

    if (ValidateOnly)
      throw new InvalidOperationException(
        $"Component manifest '{OutputPath}' is stale or has no matching discovery inventory. Build its producing project before packing with --no-build."
      );

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputPath))!);
    var requestPath = OutputPath + ".request.json";
    var rawManifestPath = OutputPath + ".raw.json";
    ManifestIO.Write(
      requestPath,
      new DiscoveryRequest
      {
        ProjectDirectory = Path.GetFullPath(ProjectDirectory),
        AssemblyPath = Path.GetFullPath(AssemblyPath),
        CompilerArgumentsPath = Path.GetFullPath(CompilerArgumentsPath),
        TargetFramework = TargetFramework,
        RuntimeIdentifier = RuntimeIdentifier,
        OutputPath = Path.GetFullPath(rawManifestPath),
      }
    );
    var start = new ProcessStartInfo(DotnetHost)
    {
      WorkingDirectory = ProjectDirectory,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
    };
    start.ArgumentList.Add(Path.GetFullPath(AnalysisAssembly));
    start.ArgumentList.Add(Path.GetFullPath(requestPath));
    using var process =
      Process.Start(start)
      ?? throw new InvalidOperationException("Could not start the component discovery tool.");
    var stdout = process.StandardOutput.ReadToEndAsync(_cancellation.Token);
    var stderr = process.StandardError.ReadToEndAsync(_cancellation.Token);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
    try
    {
      await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      if (!process.HasExited)
        process.Kill(entireProcessTree: true);
      await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
      throw new InvalidOperationException(
        $"Component discovery was cancelled or exceeded AtliTailwindDiscoveryTimeoutSeconds ({TimeoutSeconds})."
      );
    }
    var diagnostic = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
    if (process.ExitCode != 0)
      throw new InvalidOperationException(
        $"Component discovery failed (exit {process.ExitCode}). {diagnostic.Trim()}"
      );
    var manifest = ValidateManifest(rawManifestPath, AssemblyPath);
    ManifestDeclarations.ApplyOwned(
      manifest,
      componentAdditions,
      sourceAdditions,
      ProjectDirectory
    );
    ManifestIO.Write(OutputPath, manifest);
    ManifestIO.WriteText(statePath, fingerprint);
    GeneratedFiles =
    [
      new TaskItem(OutputPath),
      new TaskItem(statePath),
      new TaskItem(requestPath),
      new TaskItem(rawManifestPath),
    ];
    Log.LogMessage(
      MessageImportance.Normal,
      "Tailwind discovery: {0} components in {1}",
      manifest.Components.Count,
      manifest.Artifact.AssemblyName
    );
    if (!string.IsNullOrWhiteSpace(diagnostic))
      Log.LogMessage(MessageImportance.Low, "{0}", diagnostic.Trim());
  }
}
