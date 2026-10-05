using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Atli.Reports.Blazor.Tailwind.Build;

/// <summary>Builds isolated Tailwind bundles using the official standalone compiler.</summary>
public sealed partial class BuildTailwindCss
  : Microsoft.Build.Utilities.Task,
    ICancelableTask,
    IDisposable
{
  private readonly CancellationTokenSource cancellation = new();
  private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

  public ITaskItem[] Inputs { get; set; } = [];

  [Required]
  public string ProjectDirectory { get; set; } = "";

  [Required]
  public string IntermediateDirectory { get; set; } = "";

  [Required]
  public string OutputDirectory { get; set; } = "";
  public string PublishDirectory { get; set; } = "";
  public string Executable { get; set; } = "";
  public string Version { get; set; } = "4.3.3";
  public string CacheDirectory { get; set; } = "";
  public bool Minify { get; set; } = true;
  public bool Compile { get; set; } = true;
  public int TimeoutSeconds { get; set; } = 120;

  [Output]
  public ITaskItem[] GeneratedFiles { get; private set; } = [];

  public void Cancel() => cancellation.Cancel();

  public void Dispose() => cancellation.Dispose();

  public override bool Execute()
  {
    try
    {
      if (TimeoutSeconds <= 0)
        throw new InvalidOperationException(
          "AtliTailwindTimeoutSeconds must be greater than zero."
        );
      ExecuteAsync(cancellation.Token).GetAwaiter().GetResult();
      return !Log.HasLoggedErrors;
    }
    catch (Exception exception)
      when (exception
          is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or HttpRequestException
            or OperationCanceledException
            or Win32Exception
            or CryptographicException
      )
    {
      Log.LogError("Atli Tailwind: {0}", exception.Message);
      return false;
    }
  }

  private async System.Threading.Tasks.Task ExecuteAsync(CancellationToken token)
  {
    var intermediate = Path.GetFullPath(IntermediateDirectory, ProjectDirectory);
    var output = Path.GetFullPath(OutputDirectory, ProjectDirectory);
    var bundles = new Dictionary<string, BundleInput>(StringComparer.OrdinalIgnoreCase);
    foreach (var input in Inputs)
    {
      var inputPath = Path.GetFullPath(input.ItemSpec, ProjectDirectory);
      if (!File.Exists(inputPath))
        throw new InvalidOperationException($"Input '{inputPath}' does not exist.");
      var bundle = input.GetMetadata("BundlePath");
      if (string.IsNullOrEmpty(bundle))
      {
        var relative = Path.GetRelativePath(ProjectDirectory, inputPath);
        if (!relative.EndsWith(".tailwind.css", StringComparison.OrdinalIgnoreCase))
          throw new InvalidOperationException(
            $"Input '{input.ItemSpec}' must end in .tailwind.css or specify BundlePath."
          );
        bundle = relative[..^".tailwind.css".Length];
      }
      bundle = ValidateBundlePath(bundle);
      var generated = new TaskItem(Path.Combine(intermediate, bundle + ".css"));
      generated.SetMetadata("TargetPath", "tailwind/" + bundle + ".css");
      generated.SetMetadata("Link", "tailwind/" + bundle + ".css");
      if (!bundles.TryAdd(bundle, new BundleInput(bundle, inputPath, generated, input)))
        throw new InvalidOperationException(
          $"Duplicate Tailwind BundlePath '{bundle}'. Give every AtliTailwind input a unique BundlePath (or disable EnableDefaultAtliTailwindItems when declaring inputs explicitly)."
        );
    }
    GeneratedFiles = bundles.Values.Select(bundle => (ITaskItem)bundle.Output).ToArray();
    ValidateDiscoveryDeclarations(bundles);
    if (!Compile)
    {
      foreach (var generated in GeneratedFiles)
        if (!File.Exists(generated.ItemSpec))
          throw new InvalidOperationException(
            $"Compiled Tailwind stylesheet '{generated.ItemSpec}' is missing. Run dotnet build with the same configuration before publishing with --no-build."
          );
      ValidateDiscoveryPublish(intermediate, bundles);
      if (!string.IsNullOrWhiteSpace(PublishDirectory))
      {
        var publish = Path.GetFullPath(PublishDirectory, ProjectDirectory);
        var destinationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publish)));
        PruneOutputs(
          Path.Combine(intermediate, "publish-" + destinationId + ".txt"),
          bundles.Keys,
          Path.Combine(publish, "tailwind")
        );
      }
      return;
    }

    var discovery = PrepareDiscovery(intermediate, bundles);
    // Manual inputs and unrestricted CSS dependencies rebuild conservatively. Graph-only inputs
    // with an exact source(none) import can reuse a verified candidate/compiler fingerprint.
    if (bundles.Count > 0)
    {
      string? compiler = null;
      foreach (var bundle in bundles.Values)
      {
        token.ThrowIfCancellationRequested();
        var input = bundle.Input;
        var generated = bundle.Output;
        if (discovery.Bundles.TryGetValue(bundle.Name, out var graphBundle))
        {
          if (graphBundle.SkipCompilation)
          {
            Log.LogMessage(
              MessageImportance.Normal,
              "Tailwind: {0} unchanged (component graph)",
              bundle.Name
            );
            continue;
          }
          input = graphBundle.CompilerInput;
        }
        compiler ??= await ResolveCompilerAsync(token).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(generated.ItemSpec)!);
        var temporary = generated.ItemSpec + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
          await RunCompilerAsync(compiler, input, temporary, token).ConfigureAwait(false);
          if (!File.Exists(temporary))
            throw new InvalidOperationException($"Tailwind did not produce CSS for '{input}'.");
          // Preserve the previous file's timestamp if the output has not changed.
          if (!File.Exists(generated.ItemSpec) || !FilesEqual(temporary, generated.ItemSpec))
            File.Move(temporary, generated.ItemSpec, overwrite: true);
          Log.LogMessage(
            MessageImportance.Normal,
            "Tailwind: {0} -> {1}",
            Path.GetRelativePath(ProjectDirectory, bundle.Input),
            generated.GetMetadata("TargetPath")
          );
        }
        finally
        {
          File.Delete(temporary);
        }
      }
    }
    PruneOutputs(
      Path.Combine(intermediate, "bundles.txt"),
      bundles.Keys,
      intermediate,
      Path.Combine(output, "tailwind")
    );
    CompleteDiscovery(intermediate, discovery);
  }

  private sealed record BundleInput(
    string Name,
    string Input,
    TaskItem Output,
    ITaskItem Declaration
  );

  private static void PruneOutputs(
    string manifest,
    IEnumerable<string> bundles,
    params string[] roots
  )
  {
    Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
    var current = new HashSet<string>(bundles, StringComparer.OrdinalIgnoreCase);
    if (File.Exists(manifest))
    {
      foreach (var previous in File.ReadAllLines(manifest))
      {
        var safe = ValidateBundlePath(previous);
        if (current.Contains(safe))
          continue;
        foreach (var root in roots)
        {
          var previousOutput = Path.Combine(root, safe + ".css");
          if (File.Exists(previousOutput))
            File.Delete(previousOutput);
        }
      }
    }
    File.WriteAllLines(manifest, current);
  }

  private async Task<string> ResolveCompilerAsync(CancellationToken token)
  {
    if (!string.IsNullOrWhiteSpace(Executable))
    {
      var supplied = Path.GetFullPath(Executable, ProjectDirectory);
      if (!File.Exists(supplied))
        throw new InvalidOperationException(
          $"AtliTailwindExecutable '{supplied}' does not exist. Supply a standalone Tailwind v4 CLI executable, or omit the property to download it automatically."
        );
      return supplied;
    }
    var version = Version.StartsWith('v') ? Version[1..] : Version;
    if (!StableV4Version().IsMatch(version))
      throw new InvalidOperationException(
        "AtliTailwindVersion must be a pinned Tailwind v4 version (for example 4.3.3); floating versions are not supported."
      );
    var asset = GetAssetName();
    var cacheRoot = string.IsNullOrWhiteSpace(CacheDirectory)
      ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Atli.Reports",
        "Tailwind"
      )
      : Path.GetFullPath(CacheDirectory, ProjectDirectory);
    var versionDirectory = Path.Combine(cacheRoot, version);
    Directory.CreateDirectory(versionDirectory);
    var path = Path.Combine(versionDirectory, asset);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
    timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
    await using var cacheLock = await AcquireLockAsync(path + ".lock", timeout.Token)
      .ConfigureAwait(false);
    if (File.Exists(path))
      return path;

    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
    try
    {
      var release = $"https://github.com/tailwindlabs/tailwindcss/releases/download/v{version}/";
      Log.LogMessage(
        MessageImportance.High,
        "Downloading official Tailwind CSS {0} ({1}) to {2}",
        version,
        asset,
        versionDirectory
      );
      using var response = await Http.GetAsync(
          release + asset,
          HttpCompletionOption.ResponseHeadersRead,
          timeout.Token
        )
        .ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      await using (
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
      )
        await response.Content.CopyToAsync(stream, timeout.Token).ConfigureAwait(false);

      var expected =
        version == "4.3.3"
          ? PinnedChecksums[asset]
          : await GetReleaseChecksumAsync(release, asset, timeout.Token).ConfigureAwait(false);
      await using (var stream = File.OpenRead(temporary))
      {
        var actual = Convert.ToHexString(
          await SHA256.HashDataAsync(stream, timeout.Token).ConfigureAwait(false)
        );
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
          throw new InvalidOperationException(
            $"Checksum verification failed for Tailwind {version} ({asset}). Remove the cached download and retry, or provide AtliTailwindExecutable."
          );
      }
      if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(
          temporary,
          UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute
        );
      File.Move(temporary, path);
      return path;
    }
    catch (Exception exception)
      when (exception is HttpRequestException or OperationCanceledException)
    {
      throw new InvalidOperationException(
        $"Could not download Tailwind {version} ({asset}). Check network access to GitHub, increase AtliTailwindTimeoutSeconds, or set AtliTailwindExecutable to a preinstalled standalone CLI. {exception.Message}",
        exception
      );
    }
    finally
    {
      File.Delete(temporary);
    }
  }

  private async System.Threading.Tasks.Task RunCompilerAsync(
    string compiler,
    string input,
    string output,
    CancellationToken token
  )
  {
    using var process = new Process
    {
      StartInfo = new ProcessStartInfo(compiler)
      {
        WorkingDirectory = ProjectDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
      },
    };
    foreach (var argument in new[] { "--input", input, "--output", output })
      process.StartInfo.ArgumentList.Add(argument);
    if (Minify)
      process.StartInfo.ArgumentList.Add("--minify");
    process.StartInfo.Environment["NO_COLOR"] = "1";
    process.Start();
    var stdout = process.StandardOutput.ReadToEndAsync(token);
    var stderr = process.StandardError.ReadToEndAsync(token);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
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
        $"Tailwind compilation of '{input}' was cancelled or exceeded AtliTailwindTimeoutSeconds ({TimeoutSeconds})."
      );
    }
    var diagnostic = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
    if (process.ExitCode != 0)
      throw new InvalidOperationException(
        $"Tailwind compilation failed for '{input}' (exit {process.ExitCode}). {diagnostic.Trim()}"
      );
    Log.LogMessage(MessageImportance.Low, "{0}", diagnostic.Trim());
  }

  private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken token)
  {
    while (true)
    {
      token.ThrowIfCancellationRequested();
      try
      {
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      }
      catch (IOException)
      {
        await System.Threading.Tasks.Task.Delay(100, token).ConfigureAwait(false);
      }
    }
  }

  private static async Task<string> GetReleaseChecksumAsync(
    string release,
    string asset,
    CancellationToken token
  )
  {
    var checksums = await Http.GetStringAsync(release + "sha256sums.txt", token)
      .ConfigureAwait(false);
    foreach (var line in checksums.Split('\n'))
    {
      var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (fields.Length == 2 && fields[1].TrimStart('*') == asset && fields[0].Length == 64)
        return fields[0];
    }
    throw new InvalidOperationException(
      $"The official Tailwind release has no SHA-256 checksum for '{asset}'. Set AtliTailwindExecutable to use a separately verified compiler."
    );
  }

  private static string GetAssetName()
  {
    var architecture = RuntimeInformation.ProcessArchitecture switch
    {
      Architecture.X64 => "x64",
      Architecture.Arm64 => "arm64",
      _ => throw new InvalidOperationException(
        "Tailwind standalone CLI requires an x64 or arm64 build host. Set AtliTailwindExecutable for a custom compiler."
      ),
    };
    if (OperatingSystem.IsMacOS())
      return "tailwindcss-macos-" + architecture;
    if (OperatingSystem.IsLinux())
      return "tailwindcss-linux-"
        + architecture
        + (
          RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal)
            ? "-musl"
            : ""
        );
    if (OperatingSystem.IsWindows() && architecture == "x64")
      return "tailwindcss-windows-x64.exe";
    throw new InvalidOperationException(
      "This operating system/architecture has no official Tailwind standalone CLI. Set AtliTailwindExecutable to a compatible compiler."
    );
  }

  private static string ValidateBundlePath(string bundle)
  {
    bundle = bundle.Replace('\\', '/');
    if (
      string.IsNullOrWhiteSpace(bundle)
      || bundle.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
    )
      throw new InvalidOperationException(
        $"Invalid Tailwind BundlePath '{bundle}': use a nonempty relative path without the .css extension."
      );
    foreach (var segment in bundle.Split('/'))
    {
      if (
        segment.Length == 0
        || segment is "." or ".."
        || segment.EndsWith('.')
        || segment.EndsWith(' ')
        || segment.Any(character =>
          char.IsControl(character) || "<>:\"|?*".Contains(character, StringComparison.Ordinal)
        )
        || ReservedDevice().IsMatch(segment)
      )
        throw new InvalidOperationException(
          $"Invalid Tailwind BundlePath '{bundle}': use a portable relative path with no traversal. External input files must specify BundlePath explicitly."
        );
    }
    return bundle;
  }

  private static bool FilesEqual(string first, string second)
  {
    if (new FileInfo(first).Length != new FileInfo(second).Length)
      return false;
    using var firstStream = File.OpenRead(first);
    using var secondStream = File.OpenRead(second);
    return SHA256.HashData(firstStream).AsSpan().SequenceEqual(SHA256.HashData(secondStream));
  }

  [GeneratedRegex("^4\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
  private static partial Regex StableV4Version();

  [GeneratedRegex(
    "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
  )]
  private static partial Regex ReservedDevice();

  // SHA-256 digests published with https://github.com/tailwindlabs/tailwindcss/releases/tag/v4.3.3.
  // Keep the default version and every platform digest together when upgrading the compiler.
  private static readonly Dictionary<string, string> PinnedChecksums = new(StringComparer.Ordinal)
  {
    ["tailwindcss-linux-arm64"] =
      "55fd0b241214eff3de1e8ee4f22796662f2d2e7a49bcfca7477cfd0bac398195",
    ["tailwindcss-linux-arm64-musl"] =
      "71ea4be79c9de9827545682df3e040053fb535d37c71ed2cfdedf9385a0868e0",
    ["tailwindcss-linux-x64"] = "dc61b3ac6b8c9ca874c0cc4c57b2409791a64c5540404ca5f5367360babc313a",
    ["tailwindcss-linux-x64-musl"] =
      "a04d34ceacc8f52cbe8920ad846cdeb61d3d0021dba32db0d1f77c9d9fad7a6c",
    ["tailwindcss-macos-arm64"] =
      "cdf646702987a743464dff4d9c60fd4480d1c1e73dd819a9a67f1078815dce9d",
    ["tailwindcss-macos-x64"] = "7922e0953f2110c05976e3bf58f14e643d90427575e766b7d433f5f80cbee7e1",
    ["tailwindcss-windows-x64.exe"] =
      "e0e260ce048014e9268f6237ff18f8ccf02cef521cbd0ae04e82c2cdf7aa3955",
  };
}
