using System.ComponentModel;
using Atli.Reports.Blazor.Tailwind.Contracts;

namespace Atli.Reports.Blazor.Tailwind.Build;

public abstract class DiscoveryTask : Microsoft.Build.Utilities.Task
{
  public sealed override bool Execute()
  {
    try
    {
      ExecuteCore();
      return !Log.HasLoggedErrors;
    }
    catch (Exception exception)
      when (exception
          is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or OperationCanceledException
            or Win32Exception
            or System.Text.Json.JsonException
            or BadImageFormatException
      )
    {
      Log.LogError("Atli Tailwind discovery: {0}", exception.Message);
      return false;
    }
  }

  protected abstract void ExecuteCore();

  internal static ComponentManifest ValidateManifest(string manifestPath, string implementationPath)
  {
    if (!File.Exists(manifestPath))
      throw new InvalidOperationException(
        $"Component manifest '{manifestPath}' is missing. Build its producing project with discovery enabled before packing or publishing with --no-build."
      );
    var manifest = ManifestIO.Read<ComponentManifest>(manifestPath);
    ManifestIO.Validate(manifest, manifestPath);
    if (
      !File.Exists(implementationPath)
      || !ManifestIO
        .HashFile(implementationPath)
        .Equals(manifest.Artifact.ImplementationSha256, StringComparison.OrdinalIgnoreCase)
    )
      throw new InvalidOperationException(
        $"Component manifest '{manifestPath}' does not match implementation assembly '{implementationPath}'. Rebuild the producing project; post-compilation assembly changes require a new manifest."
      );
    var assembly = System.Reflection.AssemblyName.GetAssemblyName(implementationPath);
    if (
      manifest.Artifact.AssemblyName != assembly.Name
      || manifest.Artifact.AssemblyVersion != assembly.Version?.ToString()
      || !manifest.Artifact.PublicKeyToken.Equals(
        Convert.ToHexString(assembly.GetPublicKeyToken() ?? []),
        StringComparison.OrdinalIgnoreCase
      )
    )
      throw new InvalidOperationException(
        $"Component manifest '{manifestPath}' has an assembly identity inconsistent with '{implementationPath}'."
      );
    return manifest;
  }
}
