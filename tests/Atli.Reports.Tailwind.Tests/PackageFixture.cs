using System.Diagnostics;
using System.Xml.Linq;
using TUnit.Core.Interfaces;

namespace Atli.Reports.Tailwind.Tests;

/// <summary>
/// Packs the actual packages, then restores them from consumers outside the repository. No project
/// references or repository build properties can accidentally make the package work.
/// </summary>
public sealed class PackageFixture : IAsyncInitializer, IAsyncDisposable
{
  private readonly string _root = Path.Combine(
    CanonicalTemporaryDirectory(),
    "atli tailwind acceptance",
    Guid.NewGuid().ToString("N")
  );

  private static string CanonicalTemporaryDirectory()
  {
    // macOS exposes /var through a symlink to /private/var. The Razor SDK compares
    // project-reference paths against its working directory, which resolves that link.
    var temporary = Path.GetFullPath(Path.GetTempPath());
    for (
      var directory = new DirectoryInfo(temporary);
      directory is not null;
      directory = directory.Parent
    )
    {
      if (
        (directory.Attributes & FileAttributes.ReparsePoint) != 0
        && directory.ResolveLinkTarget(returnFinalTarget: true) is { } target
      )
      {
        return Path.Combine(target.FullName, Path.GetRelativePath(directory.FullName, temporary));
      }
    }

    return temporary;
  }

  public async Task InitializeAsync()
  {
    var repository = FindRepository();
    Directory.CreateDirectory(_root);
    File.Copy(Path.Combine(repository, "global.json"), Path.Combine(_root, "global.json"));
    var feed = Path.Combine(_root, "feed");
    foreach (
      var name in new[]
      {
        "Atli.Reports.Blazor.Components",
        "Atli.Reports.Engine",
        "Atli.Reports.Blazor",
        "Atli.Reports.Blazor.Tailwind.Discovery",
        "Atli.Reports.Blazor.Tailwind",
      }
    )
    {
      var result = await RunProcessAsync(
        repository,
        null,
        [
          "pack",
          Path.Combine(repository, "src", name, $"{name}.csproj"),
          "--configuration",
          "Release",
          "--output",
          feed,
          "--artifacts-path",
          Path.Combine(_root, "package-build"),
          "-p:Version=0.0.0-tailwindtests",
          "-p:PackageVersion=0.0.0-tailwindtests",
          "--nologo",
        ]
      );
      result.EnsureSuccess();
    }

    XDocument config = new(
      new XElement(
        "configuration",
        new XElement(
          "packageSources",
          new XElement("clear"),
          new XElement("add", new XAttribute("key", "local"), new XAttribute("value", feed)),
          new XElement(
            "add",
            new XAttribute("key", "nuget"),
            new XAttribute("value", "https://api.nuget.org/v3/index.json")
          )
        )
      )
    );
    config.Save(Path.Combine(_root, "NuGet.Config"));
  }

  internal string FeedDirectory => Path.Combine(_root, "feed");

  internal ConsumerProject CreateConsumer(string templateName = "Consumer")
  {
    var directory = Path.Combine(_root, "consumer " + Guid.NewGuid().ToString("N"));
    CopyTemplate(templateName, directory);
    return new ConsumerProject(directory, _root);
  }

  internal (ConsumerProject Leaf, ConsumerProject Outer) CreateLibraries()
  {
    var directory = Path.Combine(_root, "libraries " + Guid.NewGuid().ToString("N"));
    CopyTemplate("Libraries", directory);
    return (
      new ConsumerProject(Path.Combine(directory, "Leaf"), _root),
      new ConsumerProject(Path.Combine(directory, "Outer"), _root)
    );
  }

  private static void CopyTemplate(string templateName, string directory)
  {
    Directory.CreateDirectory(directory);
    var template = Path.Combine(AppContext.BaseDirectory, templateName);
    foreach (var source in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
    {
      var destination = Path.Combine(directory, Path.GetRelativePath(template, source));
      if (destination.EndsWith(".template", StringComparison.Ordinal))
      {
        destination = destination[..^".template".Length];
      }

      Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
      File.Copy(source, destination);
    }
  }

  public ValueTask DisposeAsync()
  {
    if (Environment.GetEnvironmentVariable("ATLI_TAILWIND_TEST_KEEP_TEMP") == "1")
    {
      Console.WriteLine($"Tailwind acceptance workspace retained at {_root}");
      return ValueTask.CompletedTask;
    }

    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }

    return ValueTask.CompletedTask;
  }

  private static string FindRepository()
  {
    foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
      for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
      {
        if (File.Exists(Path.Combine(current.FullName, "Atli.Reports.slnx")))
        {
          return current.FullName;
        }
      }
    }

    throw new DirectoryNotFoundException("Run the Tailwind acceptance tests from the repository.");
  }

  internal static async Task<ProcessResult> RunProcessAsync(
    string directory,
    Dictionary<string, string>? environment,
    string[] arguments
  )
  {
    ProcessStartInfo start = new("dotnet")
    {
      WorkingDirectory = directory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    // Build nodes must not retain the task DLL inside our temporary NuGet cache, particularly
    // on Windows where that would keep the fixture directory locked after the test completes.
    start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
    start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
    foreach (var argument in arguments)
    {
      start.ArgumentList.Add(argument);
    }

    if (environment is not null)
    {
      foreach (var (key, value) in environment)
      {
        start.Environment[key] = value;
      }
    }

    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
    try
    {
      await process.WaitForExitAsync(timeout.Token);
    }
    catch (OperationCanceledException)
    {
      process.Kill(entireProcessTree: true);
      await process.WaitForExitAsync(CancellationToken.None);
      throw new InvalidOperationException(
        $"dotnet {string.Join(' ', arguments)} timed out.\n{await output}\n{await error}"
      );
    }

    return new ProcessResult(process.ExitCode, await output + await error);
  }
}

internal sealed record ProcessResult(int ExitCode, string Output)
{
  public void EnsureSuccess()
  {
    if (ExitCode != 0)
    {
      throw new InvalidOperationException($"dotnet exited with {ExitCode}:\n{Output}");
    }
  }
}

internal sealed class ConsumerProject(string directory, string root)
{
  private static readonly string[] PdfArguments = ["--pdf"];
  public string DirectoryPath => directory;
  public string BuildOutput => Path.Combine(directory, "bin", "Release", "net10.0");
  public string PublishOutput => Path.Combine(root, "published " + Path.GetFileName(directory));

  public async Task<ProcessResult> DotnetAsync(params string[] arguments) =>
    await PackageFixture.RunProcessAsync(
      directory,
      new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(root, "packages") },
      [.. arguments, $"-p:AtliTailwindCacheDirectory={Path.Combine(root, "compiler")}", "--nologo"]
    );

  public async Task<string> RenderAsync(
    string applicationDirectory,
    bool pdf = false,
    params string[] additionalArguments
  )
  {
    var output = Path.Combine(root, "rendered " + Guid.NewGuid().ToString("N"));
    var compilerCache = Path.Combine(root, "compiler");
    var hiddenCache = compilerCache + ".unavailable";
    Directory.Move(compilerCache, hiddenCache);
    try
    {
      var result = await PackageFixture.RunProcessAsync(
        root,
        new Dictionary<string, string>
        {
          // Nothing in a deployed app should read build-time compiler settings or NuGet packages.
          ["AtliTailwindExecutable"] = Path.Combine(root, "no-compiler-installed"),
          ["NUGET_PACKAGES"] = Path.Combine(root, "no-packages-installed"),
        },
        [
          Path.Combine(applicationDirectory, "Consumer.dll"),
          output,
          .. pdf ? PdfArguments : [],
          .. additionalArguments,
        ]
      );
      result.EnsureSuccess();
    }
    finally
    {
      Directory.Move(hiddenCache, compilerCache);
    }

    return output;
  }

  public string ReadCss(string report, bool published = false) =>
    File.ReadAllText(
      Path.Combine(published ? PublishOutput : BuildOutput, "tailwind", "Reports", report + ".css")
    );

  public string Source(string path) => Path.Combine(directory, path);

  public void EditProject(Action<XElement> edit)
  {
    var projectPath = Directory.GetFiles(directory, "*.csproj").Single();
    var project = XDocument.Load(projectPath);
    edit(project.Root!);
    project.Save(projectPath);
  }

  public void Write(string path, string contents)
  {
    var file = Source(path);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, contents);
  }

  public void Replace(string path, string oldValue, string newValue)
  {
    var file = Source(path);
    File.WriteAllText(
      file,
      File.ReadAllText(file).Replace(oldValue, newValue, StringComparison.Ordinal)
    );
  }
}
