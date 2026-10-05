using System.Xml.Linq;

namespace Atli.Reports.Tailwind.Tests;

[ClassDataSource<PackageFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("tailwind-packages")]
public class ConsumerTests(PackageFixture packages)
{
  [Test]
  public async Task Builds_small_separate_bundles_and_rebuilds_changed_and_removed_sources()
  {
    var consumer = packages.CreateConsumer();
    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();

    var invoice = consumer.ReadCss("Invoice");
    var receipt = consumer.ReadCss("Receipt");
    await Assert.That(invoice).Contains(".uppercase{");
    await Assert.That(invoice).DoesNotContain(".italic{");
    await Assert.That(receipt).Contains(".italic{");
    await Assert.That(receipt).DoesNotContain(".uppercase{");
    foreach (var css in new[] { invoice, receipt })
    {
      await Assert.That(css).Contains(".font-bold{");
      await Assert.That(css).Contains(".text-brand{");
      await Assert.That(css).Contains("#123456");
    }

    var rendered = await consumer.RenderAsync(consumer.BuildOutput);
    var invoiceHtml = await File.ReadAllTextAsync(Path.Combine(rendered, "Invoice.html"));
    var receiptHtml = await File.ReadAllTextAsync(Path.Combine(rendered, "Receipt.html"));
    await Assert.That(invoiceHtml).Contains(invoice);
    await Assert.That(invoiceHtml).DoesNotContain(".italic{");
    await Assert.That(receiptHtml).Contains(receipt);
    await Assert.That(receiptHtml).DoesNotContain(".uppercase{");

    consumer.Replace("Reports/Invoice.razor", "uppercase", "capitalize");
    consumer.Replace("Shared/Header.razor", "font-bold", "font-medium");
    consumer.Replace("theme.css", "#123456", "#654321");
    (
      await consumer.DotnetAsync("build", "--configuration", "Release", "--no-restore")
    ).EnsureSuccess();

    await Assert.That(consumer.ReadCss("Invoice")).Contains(".capitalize{");
    await Assert.That(consumer.ReadCss("Invoice")).DoesNotContain(".uppercase{");
    foreach (var css in new[] { consumer.ReadCss("Invoice"), consumer.ReadCss("Receipt") })
    {
      await Assert.That(css).Contains(".font-medium{");
      await Assert.That(css).DoesNotContain(".font-bold{");
      await Assert.That(css).Contains("#654321");
      await Assert.That(css).DoesNotContain("#123456");
    }

    (
      await consumer.DotnetAsync(
        "publish",
        "--configuration",
        "Release",
        "--no-build",
        "--output",
        consumer.PublishOutput
      )
    ).EnsureSuccess();
    await Assert
      .That(File.Exists(Path.Combine(consumer.PublishOutput, "tailwind/Reports/Receipt.css")))
      .IsTrue();

    File.Delete(consumer.Source("Reports/Receipt.tailwind.css"));
    (
      await consumer.DotnetAsync("build", "--configuration", "Release", "--no-restore")
    ).EnsureSuccess();
    await Assert
      .That(File.Exists(Path.Combine(consumer.BuildOutput, "tailwind/Reports/Receipt.css")))
      .IsFalse();
    (
      await consumer.DotnetAsync(
        "publish",
        "--configuration",
        "Release",
        "--no-build",
        "--output",
        consumer.PublishOutput
      )
    ).EnsureSuccess();
    await Assert
      .That(File.Exists(Path.Combine(consumer.PublishOutput, "tailwind/Reports/Receipt.css")))
      .IsFalse();
  }

  [Test]
  public async Task Publishes_from_clean_and_without_building_and_renders_without_the_compiler()
  {
    var consumer = packages.CreateConsumer();
    (
      await consumer.DotnetAsync(
        "publish",
        "--configuration",
        "Release",
        "--output",
        consumer.PublishOutput
      )
    ).EnsureSuccess();

    var firstCss = consumer.ReadCss("Invoice", published: true);
    Directory.Delete(consumer.PublishOutput, recursive: true);
    (
      await consumer.DotnetAsync(
        "publish",
        "--configuration",
        "Release",
        "--no-build",
        "--output",
        consumer.PublishOutput,
        $"-p:AtliTailwindExecutable={consumer.Source("missing-compiler")}"
      )
    ).EnsureSuccess();
    await Assert.That(consumer.ReadCss("Invoice", published: true)).IsEqualTo(firstCss);

    Directory.Delete(consumer.BuildOutput, recursive: true);
    var rendered = await consumer.RenderAsync(consumer.PublishOutput);
    var html = await File.ReadAllTextAsync(Path.Combine(rendered, "Invoice.html"));
    await Assert.That(html).Contains(firstCss);
    await Assert.That(html).Contains("Shared report heading");
    await Assert.That(html).DoesNotContain(".italic{");
  }

  [Test]
  public async Task Rejects_duplicate_bundle_paths_with_an_actionable_build_error()
  {
    var consumer = packages.CreateConsumer();
    var projectFile = consumer.Source("Consumer.csproj");
    var project = XDocument.Load(projectFile);
    project.Root!.Add(
      new XElement("PropertyGroup", new XElement("EnableDefaultAtliTailwindItems", "false")),
      new XElement(
        "ItemGroup",
        new XElement(
          "AtliTailwind",
          new XAttribute("Include", "Reports/*.tailwind.css"),
          new XAttribute("BundlePath", "Reports/Same")
        )
      )
    );
    project.Save(projectFile);

    var result = await consumer.DotnetAsync("build", "--configuration", "Release");
    await Assert.That(result.ExitCode).IsNotEqualTo(0);
    await Assert.That(result.Output).Contains("Reports/Same");
    await Assert.That(result.Output.ToUpperInvariant()).Contains("DUPLICATE");
  }

  [Test]
  public async Task Explicit_external_inputs_honor_default_discovery_opt_out_and_bundle_paths()
  {
    var consumer = packages.CreateConsumer();
    var externalInput = consumer.DirectoryPath + ".tailwind.css";
    // This stylesheet lives beside the consumer directory, as a shared project input would.
    var relativeSource = Path.GetFileName(consumer.DirectoryPath) + "/Reports/Invoice.razor";
    await File.WriteAllTextAsync(
      externalInput,
      $"@import \"tailwindcss\" source(none);\n@source \"./{relativeSource}\";\n"
    );
    var projectFile = consumer.Source("Consumer.csproj");
    var project = XDocument.Load(projectFile);
    project.Root!.Add(
      new XElement("PropertyGroup", new XElement("EnableDefaultAtliTailwindItems", "false")),
      new XElement(
        "ItemGroup",
        new XElement(
          "AtliTailwind",
          new XAttribute("Include", externalInput),
          new XAttribute("BundlePath", "Selected/OnlyInvoice")
        )
      )
    );
    project.Save(projectFile);

    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();
    var output = Path.Combine(consumer.BuildOutput, "tailwind");
    var files = Directory.GetFiles(output, "*.css", SearchOption.AllDirectories);
    await Assert.That(files.Length).IsEqualTo(1);
    var css = await File.ReadAllTextAsync(Path.Combine(output, "Selected/OnlyInvoice.css"));
    await Assert.That(css).Contains(".uppercase{");
    await Assert.That(css).DoesNotContain(".italic{");
  }

  [Test]
  public async Task Default_inputs_allow_custom_bundle_paths_using_item_updates()
  {
    var consumer = packages.CreateConsumer();
    var projectFile = consumer.Source("Consumer.csproj");
    var project = XDocument.Load(projectFile);
    project.Root!.Add(
      new XElement(
        "ItemGroup",
        new XElement(
          "AtliTailwind",
          new XAttribute("Update", "Reports/Invoice.tailwind.css"),
          new XAttribute("BundlePath", "Selected/RenamedInvoice")
        )
      )
    );
    project.Save(projectFile);

    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();
    var css = await File.ReadAllTextAsync(
      Path.Combine(consumer.BuildOutput, "tailwind/Selected/RenamedInvoice.css")
    );
    await Assert.That(css).Contains(".uppercase{");
    await Assert.That(consumer.ReadCss("Receipt")).Contains(".italic{");
    await Assert
      .That(File.Exists(Path.Combine(consumer.BuildOutput, "tailwind/Reports/Invoice.css")))
      .IsFalse();
  }

  [Test]
  public async Task No_build_publish_reports_missing_generated_css()
  {
    var consumer = packages.CreateConsumer();
    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();
    var generatedFiles = Directory.GetFiles(
      consumer.Source("obj"),
      "Invoice.css",
      SearchOption.AllDirectories
    );
    await Assert.That(generatedFiles.Length).IsEqualTo(1);
    await Assert
      .That(Path.GetRelativePath(consumer.Source("obj"), generatedFiles[0]))
      .StartsWith("Release" + Path.DirectorySeparatorChar);
    foreach (var generated in generatedFiles)
    {
      File.Delete(generated);
    }

    var result = await consumer.DotnetAsync(
      "publish",
      "--configuration",
      "Release",
      "--no-build",
      "--output",
      consumer.PublishOutput
    );
    await Assert.That(result.ExitCode).IsNotEqualTo(0);
    await Assert.That(result.Output).Contains("Invoice");
    await Assert.That(result.Output.ToUpperInvariant()).Contains("BUILD");
  }

  [Test]
  [Category("Browser")]
  public async Task Tailwind_utilities_style_the_actual_rendered_report()
  {
    var consumer = packages.CreateConsumer();
    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();

    // Invoice.razor calls completed() only after getComputedStyle confirms the report width,
    // underline, and shared child's font weight. A missing or incorrectly inlined CSS bundle
    // therefore fails conversion with SignalTimeout instead of merely producing a valid PDF.
    var rendered = await consumer.RenderAsync(consumer.BuildOutput, pdf: true);
    var pdf = await File.ReadAllBytesAsync(Path.Combine(rendered, "Invoice.pdf"));
    await Assert.That(System.Text.Encoding.ASCII.GetString(pdf, 0, 5)).IsEqualTo("%PDF-");
  }
}
