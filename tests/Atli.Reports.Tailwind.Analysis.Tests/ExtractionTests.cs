using System.Diagnostics;
using System.Text.Json;
using Atli.Reports.Blazor.Tailwind.Analysis;
using Atli.Reports.Blazor.Tailwind.Contracts;

namespace Atli.Reports.Tailwind.Analysis.Tests;

[NotInParallel]
public class ExtractionTests
{
  private static readonly string[] ExpectedDependencies =
  [
    "Fixture.GenericPanel`1",
    "Fixture.Leaf",
    "Fixture.StaticChild",
    "Fixture.StyledBase",
  ];

  [Test]
  [Arguments("Debug", "portable")]
  [Arguments("Release", "portable")]
  [Arguments("Debug", "embedded")]
  [Arguments("Release", "embedded")]
  public async Task Reconstructs_real_Razor_and_preserves_component_ownership(
    string configuration,
    string symbols
  )
  {
    using var fixture = new Fixture();
    await fixture.BuildAsync(configuration, symbols);
    var manifest = fixture.Extract();
    var root = manifest.Components.Single(component => component.TypeName == "Fixture.Root");
    await Assert
      .That(root.Dependencies.Select(dependency => dependency.TypeName).ToArray())
      .IsEquivalentTo(ExpectedDependencies);
    await Assert.That(root.CandidateFragments).Contains("bg-red-500");
    await Assert.That(root.CandidateFragments).Contains("before:content-['hello_world']");
    await Assert.That(string.Join('\n', root.CandidateFragments)).DoesNotContain("bg-fuchsia-700");
    await Assert
      .That(string.Join('\n', root.CandidateFragments))
      .DoesNotContain("private-value-must-not-enter-manifest");
    await Assert.That(root.CandidateFragments).Contains("font-semibold leading-tight");
    await Assert.That(root.CandidateFragments).Contains("align-middle");
    await Assert.That(root.CandidateFragments).Contains("underline");
    await Assert.That(root.CandidateFragments).Contains("line-through");
    await Assert.That(root.CandidateFragments).Contains("tracking-widest");
    await Assert.That(root.CandidateFragments).Contains("rounded-lg");
    await Assert
      .That(string.Join('\n', root.CandidateFragments))
      .DoesNotContain("static-private-must-not-enter-manifest");
    await Assert
      .That(string.Join('\n', root.CandidateFragments))
      .DoesNotContain("visible-private-must-not-enter-manifest");
    await Assert.That(root.CandidateFragments).Contains("antialiased");
    await Assert.That(root.CandidateFragments).DoesNotContain("italic");
    await Assert.That(root.Unresolved.Count).IsEqualTo(2);
    await Assert.That(root.Unresolved[0].Kind).IsEqualTo("dynamic-component");
    await Assert.That(root.Unresolved[0].Location).DoesNotContain(fixture.Directory);
    var panel = manifest.Components.Single(component =>
      component.TypeName == "Fixture.GenericPanel`1"
    );
    await Assert.That(string.Join('\n', panel.CandidateFragments)).Contains("grid grid-cols-2");
    await Assert.That(panel.Dependencies.Count).IsEqualTo(0);
    var baseComponent = manifest.Components.Single(component =>
      component.TypeName == "Fixture.StyledBase"
    );
    await Assert
      .That(baseComponent.Dependencies.Single().TypeName)
      .IsEqualTo("Fixture.InheritedLeaf");
    var inherited = manifest.Components.Single(component =>
      component.TypeName == "Fixture.InheritedLeaf"
    );
    await Assert.That(inherited.CandidateFragments).Contains("italic");
    await Assert
      .That(string.Join('\n', inherited.CandidateFragments))
      .DoesNotContain("manual-private-must-not-enter-manifest");
  }

  [Test]
  public async Task Replays_pathmapped_sources_and_excludes_line_directive_documents()
  {
    using var fixture = new Fixture();
    await fixture.BuildAsync("Release", "portable", pathMap: true);
    var manifest = fixture.Extract();
    var mapped = manifest.Components.Single(component =>
      component.TypeName == "Fixture.MappedComponent"
    );
    await Assert.That(mapped.CandidateFragments).Contains("sr-only");
    await Assert
      .That(
        manifest
          .Components.Single(component => component.TypeName == "Fixture.Root")
          .Unresolved[0]
          .Location
      )
      .DoesNotContain(fixture.Directory);
  }

  [Test]
  public async Task Rejects_reference_identity_mismatch()
  {
    using var fixture = new Fixture();
    await fixture.BuildAsync("Debug", "portable");
    var snapshot = ManifestIO.Read<CompilerSnapshot>(fixture.Request.CompilerArgumentsPath);
    var originalArguments = snapshot.Arguments;
    snapshot.Arguments = snapshot
      .Arguments.Select(argument =>
        argument.Contains("Microsoft.AspNetCore.Components.dll", StringComparison.Ordinal)
          ? "/reference:" + fixture.Request.AssemblyPath
          : argument
      )
      .ToArray();
    ManifestIO.Write(fixture.Request.CompilerArgumentsPath, snapshot);
    await Assert
      .That(() => fixture.Extract())
      .Throws<InvalidDataException>()
      .WithMessageContaining("reference identity");
    snapshot.Arguments = originalArguments
      .Select(argument =>
        argument.Contains("Microsoft.AspNetCore.Components.dll", StringComparison.Ordinal)
          ? argument.Replace("/reference:", "/reference:changed_alias=", StringComparison.Ordinal)
          : argument
      )
      .ToArray();
    ManifestIO.Write(fixture.Request.CompilerArgumentsPath, snapshot);
    await Assert
      .That(() => fixture.Extract())
      .Throws<InvalidDataException>()
      .WithMessageContaining("alias or interop");
  }

  [Test]
  public async Task Rejects_a_mismatched_PE_and_PDB_pair()
  {
    using var fixture = new Fixture();
    await fixture.BuildAsync("Debug", "portable");
    var pdb = Path.ChangeExtension(fixture.Request.AssemblyPath, ".pdb");
    var originalSymbols = await File.ReadAllBytesAsync(pdb);
    await File.AppendAllTextAsync(
      Path.Combine(fixture.Directory, "Root.razor.cs"),
      "\n// Force a changed PE/PDB pair.\n"
    );
    await fixture.BuildAsync("Debug", "portable");
    await File.WriteAllBytesAsync(pdb, originalSymbols);
    await Assert
      .That(() => fixture.Extract())
      .Throws<InvalidDataException>()
      .WithMessageContaining("PDB");
  }

  [Test]
  public async Task Rejects_changed_original_source_and_compiler_options()
  {
    using var fixture = new Fixture();
    await fixture.BuildAsync("Debug", "portable");
    var source = Path.Combine(fixture.Directory, "Root.razor.cs");
    var original = await File.ReadAllTextAsync(source);
    await File.AppendAllTextAsync(source, "\n// Changed after compilation\n");
    await Assert
      .That(() => fixture.Extract())
      .Throws<InvalidDataException>()
      .WithMessageContaining("checksum mismatch");
    await File.WriteAllTextAsync(source, original);
    var snapshot = ManifestIO.Read<CompilerSnapshot>(fixture.Request.CompilerArgumentsPath);
    snapshot.Arguments =
    [
      .. snapshot.Arguments.Where(argument =>
        !argument.StartsWith("/define:", StringComparison.Ordinal)
      ),
      "/define:CHANGED",
    ];
    ManifestIO.Write(fixture.Request.CompilerArgumentsPath, snapshot);
    await Assert
      .That(() => fixture.Extract())
      .Throws<InvalidDataException>()
      .WithMessageContaining("conditional symbols");
  }

  [Test]
  public async Task Rejects_missing_symbols_and_removes_deleted_components_without_reading_stale_files()
  {
    using var fixture = new Fixture();
    await fixture.BuildAsync("Debug", "portable");
    var assembly = fixture.Request.AssemblyPath;
    var pdb = Path.ChangeExtension(assembly, ".pdb");
    var pdbBytes = await File.ReadAllBytesAsync(pdb);
    File.Delete(pdb);
    await Assert
      .That(() => fixture.Extract())
      .Throws<InvalidDataException>()
      .WithMessageContaining("PDB");
    await File.WriteAllBytesAsync(pdb, pdbBytes);
    File.Delete(Path.Combine(fixture.Directory, "Unrelated.razor"));
    // No filesystem glob may reintroduce this stale generated file into the compilation.
    await File.WriteAllTextAsync(
      Path.Combine(fixture.Directory, "obj", "stale.razor.g.cs"),
      "not valid C# bg-fuchsia-700"
    );
    await fixture.BuildAsync("Debug", "portable");
    await Assert
      .That(
        fixture.Extract().Components.Any(component => component.TypeName == "Fixture.Unrelated")
      )
      .IsFalse();
    await fixture.BuildAsync("Release", "none");
    await Assert
      .That(() => fixture.Extract())
      .Throws<InvalidDataException>()
      .WithMessageContaining("PDB");
  }

  private sealed class Fixture : IDisposable
  {
    public string Directory { get; } =
      Path.Combine(Path.GetTempPath(), "atli-analysis-" + Guid.NewGuid().ToString("N"));
    public DiscoveryRequest Request { get; private set; } = new();

    public Fixture()
    {
      System.IO.Directory.CreateDirectory(Directory);
      var source = Path.Combine(AppContext.BaseDirectory, "Fixtures");
      foreach (
        var path in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
      )
      {
        var destination = Path.Combine(
          Directory,
          Path.GetRelativePath(source, path)
            .Replace(".csproj.template", ".csproj", StringComparison.Ordinal)
        );
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(path, destination);
      }
    }

    public async Task BuildAsync(string configuration, string symbols, bool pathMap = false)
    {
      using var process = new Process
      {
        StartInfo = new ProcessStartInfo("dotnet")
        {
          WorkingDirectory = Directory,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
        },
      };
      foreach (
        var argument in new[]
        {
          "msbuild",
          "Fixture.csproj",
          "-restore",
          "-t:Rebuild",
          "-p:ProvideCommandLineArgs=true",
          "-p:Configuration=" + configuration,
          "-p:DebugType=" + symbols,
          "-getItem:CscCommandLineArgs",
          "-nologo",
          "-v:quiet",
        }
      )
        process.StartInfo.ArgumentList.Add(argument);
      if (pathMap)
        process.StartInfo.ArgumentList.Add("-p:PathMap=" + Directory + "=/_/fixture");
      process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
      process.Start();
      var outputTask = process.StandardOutput.ReadToEndAsync();
      var errorTask = process.StandardError.ReadToEndAsync();
      using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
      try
      {
        await process.WaitForExitAsync(timeout.Token);
      }
      catch (OperationCanceledException)
      {
        process.Kill(entireProcessTree: true);
        throw;
      }
      var output = await outputTask;
      var error = await errorTask;
      if (process.ExitCode != 0)
        throw new InvalidOperationException(output + error);
      var jsonStart = output.IndexOf("{\n  \"Items\"", StringComparison.Ordinal);
      if (jsonStart < 0)
        jsonStart = output.IndexOf("{\r\n  \"Items\"", StringComparison.Ordinal);
      using var json = JsonDocument.Parse(output[jsonStart..]);
      var arguments = json
        .RootElement.GetProperty("Items")
        .GetProperty("CscCommandLineArgs")
        .EnumerateArray()
        .Select(item => item.GetProperty("Identity").GetString()!)
        .ToArray();
      var snapshotPath = Path.Combine(Directory, "snapshot.json");
      ManifestIO.Write(
        snapshotPath,
        new CompilerSnapshot { ProjectDirectory = Directory, Arguments = arguments }
      );
      Request = new DiscoveryRequest
      {
        ProjectDirectory = Directory,
        CompilerArgumentsPath = snapshotPath,
        AssemblyPath = Path.Combine(Directory, "bin", configuration, "net10.0", "Fixture.dll"),
        TargetFramework = "net10.0",
        OutputPath = Path.Combine(Directory, "manifest.json"),
      };
    }

    public ComponentManifest Extract() =>
      ComponentExtractor.Extract(CompilationReader.Read(Request), Request);

    public void Dispose()
    {
      try
      {
        System.IO.Directory.Delete(Directory, recursive: true);
      }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
  }
}
