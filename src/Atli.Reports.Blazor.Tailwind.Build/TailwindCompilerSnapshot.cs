using Atli.Reports.Blazor.Tailwind.Contracts;
using Microsoft.Build.Framework;

namespace Atli.Reports.Blazor.Tailwind.Build;

public sealed class PrepareTailwindCompilerSnapshot : DiscoveryTask
{
  [Required]
  public string SnapshotPath { get; set; } = "";

  [Required]
  public string AssemblyPath { get; set; } = "";

  [Required]
  public string ProjectDirectory { get; set; } = "";

  protected override void ExecuteCore()
  {
    var valid = false;
    if (
      File.Exists(SnapshotPath)
      && File.Exists(AssemblyPath)
      && File.Exists(SnapshotPath + ".identity.json")
    )
    {
      try
      {
        var snapshot = ManifestIO.Read<CompilerSnapshot>(SnapshotPath);
        var identity = ManifestIO.Read<SnapshotIdentity>(SnapshotPath + ".identity.json");
        valid =
          snapshot.Arguments.Length > 0
          && Path.GetFullPath(snapshot.ProjectDirectory) == Path.GetFullPath(ProjectDirectory)
          && identity.SnapshotSha256 == ManifestIO.HashFile(SnapshotPath)
          && identity.AssemblySha256 == ManifestIO.HashFile(AssemblyPath);
      }
      catch (Exception exception)
        when (exception
            is IOException
              or InvalidOperationException
              or ArgumentException
              or System.Text.Json.JsonException
        )
      {
        Log.LogMessage(
          MessageImportance.Low,
          "Refreshing compiler snapshot: {0}",
          exception.Message
        );
      }
    }
    if (!valid)
    {
      // This file is a CustomAdditionalCompileOutput. Removing a stale snapshot makes normal
      // CoreCompile run; no nested compilation and no fabricated command line are necessary.
      if (File.Exists(SnapshotPath))
        File.Delete(SnapshotPath);
      if (File.Exists(SnapshotPath + ".identity.json"))
        File.Delete(SnapshotPath + ".identity.json");
    }
  }
}

public sealed class CaptureTailwindCompilerSnapshot : DiscoveryTask
{
  [Required]
  public ITaskItem[] Arguments { get; set; } = [];

  [Required]
  public string SnapshotPath { get; set; } = "";

  [Required]
  public string AssemblyPath { get; set; } = "";

  [Required]
  public string ProjectDirectory { get; set; } = "";

  protected override void ExecuteCore()
  {
    if (Arguments.Length == 0)
      throw new InvalidOperationException(
        "The compiler returned no arguments. Rebuild with ProvideCommandLineArgs=true."
      );
    ManifestIO.Write(
      SnapshotPath,
      new CompilerSnapshot
      {
        ProjectDirectory = Path.GetFullPath(ProjectDirectory),
        Arguments = Arguments.Select(argument => argument.ItemSpec).ToArray(),
      }
    );
    ManifestIO.Write(
      SnapshotPath + ".identity.json",
      new SnapshotIdentity
      {
        AssemblySha256 = ManifestIO.HashFile(AssemblyPath),
        SnapshotSha256 = ManifestIO.HashFile(SnapshotPath),
      }
    );
    // These are compiler outputs, so a real compilation must refresh their timestamps even if
    // its arguments or deterministic assembly bytes did not change.
    File.SetLastWriteTimeUtc(SnapshotPath, DateTime.UtcNow);
    File.SetLastWriteTimeUtc(SnapshotPath + ".identity.json", DateTime.UtcNow);
  }
}

internal sealed class SnapshotIdentity
{
  public string AssemblySha256 { get; set; } = "";
  public string SnapshotSha256 { get; set; } = "";
}
