using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

[assembly: Timeout(120_000)]

namespace Atli.Reports.Aspire.Hosting.Azure.Tests;

public class RendererImageTests
{
  [Test]
  [Arguments("{\"status\":{\"state\":\"Ready\"}}", true)]
  [Arguments("{\"status\":{\"state\":\"Failed\"}}", false)]
  [Arguments("{\"status\":{\"state\":\"Creating\"}}", false)]
  [Arguments("{\"name\":\"reports-image\",\"id\":\"disk-id\"}", false)]
  public async Task Reuses_only_completed_disk_images(string json, bool expected)
  {
    await Assert.That(ReportsRendererImageBuilder.IsReadyImage(json)).IsEqualTo(expected);
  }

  [Test]
  public async Task Stages_tracked_build_inputs_and_hashes_working_tree_changes()
  {
    using var checkout = new SourceCheckout();
    await checkout.InitializeAsync();
    using var first = new TemporaryDirectory();
    var firstName = await ReportsRendererImageBuilder.StageSourcesAsync(
      checkout.Path,
      first.Path,
      CancellationToken.None
    );
    await Assert.That(File.Exists(System.IO.Path.Combine(first.Path, "private-key"))).IsFalse();
    await Assert
      .That(
        File.Exists(System.IO.Path.Combine(first.Path, "src/Atli.Reports.Server/untracked.txt"))
      )
      .IsFalse();
    await Assert
      .That(File.ReadAllText(System.IO.Path.Combine(first.Path, "Dockerfile")))
      .IsEqualTo("FROM scratch\n");
    using var same = new TemporaryDirectory();
    var sameName = await ReportsRendererImageBuilder.StageSourcesAsync(
      checkout.Path,
      same.Path,
      CancellationToken.None
    );
    await Assert.That(sameName).IsEqualTo(firstName);
    File.WriteAllText(
      System.IO.Path.Combine(checkout.Path, "src/Atli.Reports.Server/Program.cs"),
      "changed working tree"
    );
    using var changed = new TemporaryDirectory();
    var changedName = await ReportsRendererImageBuilder.StageSourcesAsync(
      checkout.Path,
      changed.Path,
      CancellationToken.None
    );
    await Assert.That(changedName).IsNotEqualTo(firstName);
    await Assert
      .That(
        File.ReadAllText(System.IO.Path.Combine(changed.Path, "src/Atli.Reports.Server/Program.cs"))
      )
      .IsEqualTo("changed working tree");
  }

  [Test]
  public async Task Refuses_symlinked_source_directories()
  {
    using var checkout = new SourceCheckout();
    await checkout.InitializeAsync();
    using var external = new TemporaryDirectory();
    var source = System.IO.Path.Combine(checkout.Path, "src/Atli.Reports.Server");
    Directory.Move(source, System.IO.Path.Combine(external.Path, "server"));
    Directory.CreateSymbolicLink(source, System.IO.Path.Combine(external.Path, "server"));
    using var staged = new TemporaryDirectory();
    await Assert
      .That(async () =>
        await ReportsRendererImageBuilder.StageSourcesAsync(
          checkout.Path,
          staged.Path,
          CancellationToken.None
        )
      )
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Image_output_requires_deploy_but_manifest_expression_is_available_offline()
  {
    var builder = DistributedApplication.CreateBuilder(
      new DistributedApplicationOptions
      {
        Args = ["--operation", "publish"],
        DisableDashboard = true,
      }
    );
    var environment = builder.AddAzureReportsEnvironment("reports");
    var image = builder.AddReportsRendererImage("renderer-image", environment, ".");
    await Assert
      .That(image.Resource.ImageId.ValueExpression)
      .IsEqualTo("{renderer-image.outputs.imageId}");
    await Assert
      .That(async () => await image.Resource.ImageId.GetValueAsync())
      .Throws<InvalidOperationException>();
    image.Resource.ResolvedImageId = "disk-ready";
    await Assert.That(await image.Resource.ImageId.GetValueAsync()).IsEqualTo("disk-ready");
  }

  private sealed class SourceCheckout : IDisposable
  {
    private readonly TemporaryDirectory directory = new();
    public string Path => directory.Path;

    public async Task InitializeAsync()
    {
      Directory.CreateDirectory(System.IO.Path.Combine(Path, "src/Atli.Reports.Server"));
      File.WriteAllText(
        System.IO.Path.Combine(Path, "src/Atli.Reports.Server/Dockerfile"),
        "FROM scratch\n"
      );
      File.WriteAllText(
        System.IO.Path.Combine(Path, "src/Atli.Reports.Server/Program.cs"),
        "original"
      );
      File.WriteAllText(System.IO.Path.Combine(Path, "global.json"), "{}");
      await GitAsync("init");
      await GitAsync("add", "global.json", "src");
      File.WriteAllText(System.IO.Path.Combine(Path, "private-key"), "secret");
      File.WriteAllText(
        System.IO.Path.Combine(Path, "src/Atli.Reports.Server/untracked.txt"),
        "secret"
      );
    }

    private async Task GitAsync(params string[] arguments)
    {
      ProcessStartInfo start = new("git")
      {
        WorkingDirectory = Path,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
      };
      foreach (var argument in arguments)
      {
        start.ArgumentList.Add(argument);
      }
      using var process = Process.Start(start)!;
      await process.WaitForExitAsync();
      if (process.ExitCode != 0)
      {
        throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
      }
    }

    public void Dispose() => directory.Dispose();
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    public string Path { get; } = Directory.CreateTempSubdirectory("reports-image-test-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
  }
}
