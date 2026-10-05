using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace Atli.Reports.Tailwind.Tests;

[ClassDataSource<PackageFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("tailwind-packages")]
[Category("Discovery")]
public class ProducerTests(PackageFixture packages)
{
  [Test]
  public async Task Multitarget_packages_match_normalized_assets_and_require_each_built_manifest()
  {
    var library = packages.CreateLibraries().Leaf;
    library.EditProject(project =>
    {
      project.Descendants("TargetFramework").Remove();
      project.Descendants("PackageId").Single().Value = "Fixture.AtliTailwind.MultiTarget";
      project.Add(
        new XElement(
          "PropertyGroup",
          new XElement("TargetFrameworks", "net10.0;net10.0-windows"),
          new XElement("EnableWindowsTargeting", "true")
        )
      );
    });
    (
      await library.DotnetAsync(
        "pack",
        "--configuration",
        "Release",
        "--output",
        packages.FeedDirectory
      )
    ).EnsureSuccess();
    var packagePath = Directory
      .GetFiles(packages.FeedDirectory, "Fixture.AtliTailwind.MultiTarget.*.nupkg")
      .Single();
    using (var package = ZipFile.OpenRead(packagePath))
    {
      foreach (var framework in new[] { "net10.0", "net10.0-windows7.0" })
      {
        using var indexStream = package
          .GetEntry($"atli-tailwind/v1/{framework}/index.json")!
          .Open();
        using var index = JsonDocument.Parse(indexStream);
        var export = index.RootElement.GetProperty("exports").EnumerateArray().Single();
        var assemblyPath = export.GetProperty("assemblyPath").GetString()!;
        var manifestPath = export.GetProperty("manifestPath").GetString()!;
        await Assert.That(assemblyPath).IsEqualTo($"lib/{framework}/Fixture.Leaf.dll");
        await Assert.That(manifestPath).IsEqualTo($"atli-tailwind/v1/{framework}/manifest.json");
        using var assembly = package.GetEntry(assemblyPath)!.Open();
        using var manifestStream = package.GetEntry(manifestPath)!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var artifactHash = manifest
          .RootElement.GetProperty("artifact")
          .GetProperty("implementationSha256")
          .GetString();
        await Assert
          .That(artifactHash)
          .IsEqualTo(Convert.ToHexString(await SHA256.HashDataAsync(assembly)));
      }
    }

    var candidates = File.ReadAllText(library.Source("heading-candidates.txt"));
    library.Write("heading-candidates.txt", "outline-offset-9");
    var staleSource = await library.DotnetAsync(
      "pack",
      "--configuration",
      "Release",
      "--no-build",
      "--output",
      packages.FeedDirectory
    );
    await Assert.That(staleSource.ExitCode).IsNotEqualTo(0);
    await Assert.That(staleSource.Output.ToUpperInvariant()).Contains("BUILD");
    library.Write("heading-candidates.txt", candidates);

    File.Delete(
      library.Source("obj/Release/net10.0-windows/atli-tailwind/discovery/manifest.json")
    );
    var missing = await library.DotnetAsync(
      "pack",
      "--configuration",
      "Release",
      "--no-build",
      "--output",
      packages.FeedDirectory
    );
    await Assert.That(missing.ExitCode).IsNotEqualTo(0);
    await Assert.That(missing.Output.ToUpperInvariant()).Contains("MANIFEST");
    await Assert.That(missing.Output.ToUpperInvariant()).Contains("BUILD");
  }
}
