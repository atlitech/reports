using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Atli.Reports.Blazor.Tailwind.Contracts;

public static class ManifestIO
{
  public static JsonSerializerOptions JsonOptions { get; } =
    new()
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      PropertyNameCaseInsensitive = true,
      WriteIndented = true,
      RespectNullableAnnotations = true,
    };

  public static T Read<T>(string path)
  {
    try
    {
      return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"Discovery file '{path}' is empty.");
    }
    catch (JsonException exception)
    {
      throw new InvalidOperationException(
        $"Discovery file '{path}' contains invalid JSON: {exception.Message}",
        exception
      );
    }
  }

  public static void Write<T>(string path, T value) =>
    WriteText(path, JsonSerializer.Serialize(value, JsonOptions) + "\n");

  public static void WriteText(string path, string text)
  {
    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
    if (File.Exists(path) && File.ReadAllText(path) == text)
      return;
    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
      File.WriteAllText(temporary, text, new UTF8Encoding(false));
      File.Move(temporary, path, overwrite: true);
    }
    finally
    {
      File.Delete(temporary);
    }
  }

  public static string HashFile(string path)
  {
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
  }

  public static string HashText(string text) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

  public static void Validate(ComponentManifest manifest, string description)
  {
    if (!Version.TryParse(manifest.SchemaVersion, out var version) || version.Major != 1)
      throw new InvalidOperationException(
        $"Manifest '{description}' has unsupported schema '{manifest.SchemaVersion}'; expected major version 1."
      );
    foreach (var capability in manifest.RequiredCapabilities)
      if (capability is not ("component-graph-v1" or "candidate-fragments-v1"))
        throw new InvalidOperationException(
          $"Manifest '{description}' requires unsupported capability '{capability}'."
        );
    if (
      string.IsNullOrWhiteSpace(manifest.Artifact.AssemblyName)
      || manifest.Artifact.ImplementationSha256.Length != 64
      || !manifest.Artifact.ImplementationSha256.All(Uri.IsHexDigit)
    )
      throw new InvalidOperationException(
        $"Manifest '{description}' has an invalid assembly identity or implementation hash."
      );
    var identities = new HashSet<string>(StringComparer.Ordinal);
    foreach (var component in manifest.Components)
    {
      if (component is null)
        throw new InvalidOperationException($"Manifest '{description}' contains a null component.");
      if (string.IsNullOrWhiteSpace(component.TypeName) || !identities.Add(component.TypeName))
        throw new InvalidOperationException(
          $"Manifest '{description}' contains an empty or duplicate component '{component.TypeName}'."
        );
      foreach (var dependency in component.Dependencies)
        if (
          dependency is null
          || string.IsNullOrWhiteSpace(dependency.AssemblyName)
          || string.IsNullOrWhiteSpace(dependency.TypeName)
        )
          throw new InvalidOperationException(
            $"Manifest '{description}' contains an invalid dependency for '{component.TypeName}'."
          );
      if (
        component.CandidateFragments.Any(fragment => fragment is null)
        || component.Unresolved.Any(diagnostic => diagnostic is null)
      )
        throw new InvalidOperationException(
          $"Manifest '{description}' contains null candidate fragments or diagnostics."
        );
    }
  }
}
