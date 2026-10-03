using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// The seccomp profile Chromium's sandbox needs in the server image: Docker's default profile plus
/// the user namespaces the sandbox creates (deploy/seccomp/chromium.json in the repository, which
/// the package embeds). Under the runtime's default profile the image's browser cannot start.
/// </summary>
/// <remarks>
/// <para>
/// Docker and Podman read a profile from a file named on their command line, so the package writes
/// its copy to the user's local application data directory, named by its content, when the container
/// starts. Local runs only: deployment targets do not take runtime arguments (see the Aspire guide).
/// </para>
/// <para>
/// The package never turns the sandbox off: where the runtime cannot apply the profile or the host
/// denies user namespaces, the server stays unready and its health details say what to do.
/// </para>
/// </remarks>
internal static class ReportsServerSeccompProfile
{
  /// <summary>The embedded profile's resource name (see the project file).</summary>
  internal const string ResourceName = "Atli.Reports.Aspire.Hosting.chromium-seccomp.json";

  /// <summary>
  /// Passes the profile to the container runtime: <c>--security-opt seccomp=&lt;file&gt;</c>.
  /// </summary>
  internal static IResourceBuilder<ReportsServerResource> WithChromiumSeccompProfile(
    this IResourceBuilder<ReportsServerResource> builder
  ) =>
    builder.WithContainerRuntimeArgs(context =>
    {
      context.Args.Add("--security-opt");
      context.Args.Add($"seccomp={WriteFile()}");
    });

  /// <summary>
  /// The profile's bytes, as the package embeds them.
  /// </summary>
  internal static byte[] Read()
  {
    using var stream =
      typeof(ReportsServerSeccompProfile).Assembly.GetManifestResourceStream(ResourceName)
      ?? throw new InvalidOperationException($"The package lacks its {ResourceName} resource.");
    using MemoryStream copy = new();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  /// <summary>
  /// Writes the profile, unless an identical copy is already there, and returns its path. The file
  /// name carries a hash of the content, so a newer package never reuses an older profile, and
  /// AppHosts that start at once write the same bytes.
  /// </summary>
  internal static string WriteFile()
  {
    var content = Read();
    var hash = Convert.ToHexStringLower(SHA256.HashData(content))[..16];
    var path = Path.Combine(Directory(), $"chromium-seccomp-{hash}.json");
    if (HasContent(path, content))
    {
      return path;
    }

    var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
    File.WriteAllBytes(temporary, content);
    try
    {
      File.Move(temporary, path, overwrite: true);
    }
    catch (IOException) when (HasContent(path, content))
    {
      // Another AppHost wrote it first, and a runtime may be reading it.
      File.Delete(temporary);
    }

    return path;
  }

  /// <summary>
  /// A directory only the current user can write to: under the local application data directory,
  /// or, without one, a new private directory in the temporary directory.
  /// </summary>
  private static string Directory()
  {
    var data = Environment.GetFolderPath(
      Environment.SpecialFolder.LocalApplicationData,
      Environment.SpecialFolderOption.Create
    );
    if (string.IsNullOrEmpty(data))
    {
      return System.IO.Directory.CreateTempSubdirectory("atli-reports-seccomp-").FullName;
    }

    var directory = Path.Combine(data, "atli-reports", "seccomp");
    System.IO.Directory.CreateDirectory(directory);
    return directory;
  }

  private static bool HasContent(string path, byte[] content)
  {
    try
    {
      return File.ReadAllBytes(path).AsSpan().SequenceEqual(content);
    }
    catch (Exception exception)
      when (exception is FileNotFoundException or DirectoryNotFoundException)
    {
      return false;
    }
  }
}
