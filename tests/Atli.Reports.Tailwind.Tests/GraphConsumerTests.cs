using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Atli.Reports.Blazor.Tailwind.Contracts;

namespace Atli.Reports.Tailwind.Tests;

[ClassDataSource<PackageFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("tailwind-packages")]
[Category("Discovery")]
public partial class GraphConsumerTests(PackageFixture packages)
{
  private static readonly string[] StampNames = ["RedStamp", "BlueStamp"];

  [Test]
  public async Task Discovers_nested_generic_template_partial_and_inherited_styles()
  {
    var consumer = packages.CreateConsumer("DiscoveryConsumer");
    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();
    var invoice = consumer.ReadCss("Invoice");
    var receipt = consumer.ReadCss("Receipt");

    foreach (
      var selector in new[]
      {
        ".uppercase{",
        ".grid{",
        ".gap-7{",
        ".text-right{",
        ".whitespace-nowrap{",
        ".leading-7{",
      }
    )
    {
      await Assert.That(invoice).Contains(selector);
      await Assert.That(receipt).DoesNotContain(selector);
    }

    foreach (var css in new[] { invoice, receipt })
    {
      await Assert.That(css).Contains(".font-bold{");
      await Assert.That(css).Contains(".tracking-widest{");
      await Assert.That(css).Contains(".text-brand{");
      await Assert.That(css).DoesNotContain(".rotate-45{");
      await Assert.That(css).DoesNotContain(".skew-x-12{");
      await Assert.That(css).DoesNotContain(".bg-red-600{");
    }

    await Assert.That(invoice).Contains("hello world");
    await Assert.That(receipt).DoesNotContain("hello world");
    await Assert.That(invoice).DoesNotContain(".italic{");
    var manifest = ManifestIO.Read<ComponentManifest>(
      Path.Combine(consumer.BuildOutput, "Consumer.dll.atli-tailwind.json")
    );
    await Assert
      .That(
        manifest.Components.Any(component =>
          component.TypeName == "DiscoveryConsumer.Shared.Grid`1"
        )
      )
      .IsTrue();
    var rendered = await consumer.RenderAsync(consumer.BuildOutput);
    var html = await File.ReadAllTextAsync(Path.Combine(rendered, "Invoice.html"));
    await Assert.That(html).Contains(invoice);
    await Assert.That(html).Contains("Shared report heading");
  }

  [Test]
  public async Task Shared_source_edits_and_removed_component_files_update_each_affected_bundle()
  {
    var consumer = packages.CreateConsumer("DiscoveryConsumer");
    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();
    consumer.Replace("Shared/BrandMark.razor", "tracking-widest", "tracking-wide");
    consumer.Replace("Reports/Invoice.razor", "<LineItem Label=\"@row\" />", "<p>@row</p>");
    File.Delete(consumer.Source("Shared/LineItem.razor"));

    (
      await consumer.DotnetAsync("build", "--configuration", "Release", "--no-restore")
    ).EnsureSuccess();
    foreach (var css in new[] { consumer.ReadCss("Invoice"), consumer.ReadCss("Receipt") })
    {
      await Assert.That(css).Contains(".tracking-wide{");
      await Assert.That(css).DoesNotContain(".tracking-widest{");
      await Assert.That(css).DoesNotContain(".text-right{");
      await Assert.That(css).DoesNotContain("hello world");
    }

    var manifest = ManifestIO.Read<ComponentManifest>(
      Path.Combine(consumer.BuildOutput, "Consumer.dll.atli-tailwind.json")
    );
    await Assert
      .That(
        manifest.Components.Any(component =>
          component.TypeName == "DiscoveryConsumer.Shared.LineItem"
        )
      )
      .IsFalse();
  }

  [Test]
  public async Task Project_references_and_source_free_transitive_packages_produce_equivalent_css()
  {
    var consumer = packages.CreateConsumer("DiscoveryConsumer");
    var libraries = packages.CreateLibraries();
    UseExternalInvoice(consumer, libraries.Outer);
    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();
    var projectCss = consumer.ReadCss("Invoice");
    await Assert.That(projectCss).Contains(".font-black{");
    await Assert.That(projectCss).Contains(".underline{");
    await Assert.That(projectCss).Contains(".outline-offset-8{");
    await Assert.That(projectCss).DoesNotContain(".rotate-45{");
    await Assert.That(projectCss).DoesNotContain(".skew-x-12{");

    foreach (var library in new[] { libraries.Leaf, libraries.Outer })
    {
      (
        await library.DotnetAsync(
          "pack",
          "--configuration",
          "Release",
          "--no-build",
          "--output",
          packages.FeedDirectory,
          "-p:AtliTailwindExecutable=compiler-must-not-be-needed"
        )
      ).EnsureSuccess();
    }

    consumer.EditProject(project =>
    {
      project.Descendants("ProjectReference").Remove();
      project.Add(
        new XElement(
          "ItemGroup",
          new XElement(
            "PackageReference",
            new XAttribute("Include", "Fixture.AtliTailwind.Outer"),
            new XAttribute("Version", "0.0.0-tailwindtests")
          )
        )
      );
    });
    Directory.Delete(Path.GetDirectoryName(libraries.Outer.DirectoryPath)!, recursive: true);
    Directory.Delete(consumer.Source("bin"), recursive: true);
    Directory.Delete(consumer.Source("obj"), recursive: true);

    (
      await consumer.DotnetAsync(
        "publish",
        "--configuration",
        "Release",
        "--output",
        consumer.PublishOutput
      )
    ).EnsureSuccess();
    await Assert.That(consumer.ReadCss("Invoice", published: true)).IsEqualTo(projectCss);
    var rendered = await consumer.RenderAsync(consumer.PublishOutput);
    var html = await File.ReadAllTextAsync(Path.Combine(rendered, "ExternalInvoice.html"));
    await Assert.That(html).Contains(projectCss);
    await Assert.That(html).Contains("Packaged nested heading");
    await Assert
      .That(
        Directory
          .GetFiles(consumer.PublishOutput, "*.atli-tailwind.json", SearchOption.AllDirectories)
          .Length
      )
      .IsEqualTo(0);
    await Assert
      .That(
        Directory
          .GetFiles(consumer.PublishOutput, "*Tailwind.Analysis*", SearchOption.AllDirectories)
          .Length
      )
      .IsEqualTo(0);
  }

  [Test]
  public async Task Dynamic_coverage_is_persisted_and_declared_alternatives_stay_scoped()
  {
    var consumer = packages.CreateConsumer("DiscoveryConsumer");
    EnableDynamicReport(consumer, declareAlternatives: false);
    var warningBuild = await consumer.DotnetAsync("build", "--configuration", "Release");
    warningBuild.EnsureSuccess();
    await Assert.That(warningBuild.Output).Contains("ATLI1001");
    var strictPublish = await consumer.DotnetAsync(
      "publish",
      "--configuration",
      "Release",
      "--no-build",
      "--output",
      consumer.PublishOutput,
      "-p:AtliTailwindStrict=true"
    );
    await Assert.That(strictPublish.ExitCode).IsNotEqualTo(0);
    await Assert.That(strictPublish.Output).Contains("DynamicReport");

    DeclareDynamicAlternatives(consumer);
    consumer.Write("Sources/dynamic.txt", "opacity-35 before:content-['owner_candidate']");
    consumer.Write("Sources/invoice.txt", "outline-double");
    consumer.EditProject(project =>
      project.Add(
        new XElement(
          "ItemGroup",
          new XElement(
            "AtliTailwindSource",
            new XAttribute("Include", "Sources/dynamic.txt"),
            new XAttribute("OwnerComponent", "DiscoveryConsumer.Reports.DynamicReport")
          ),
          new XElement(
            "AtliTailwindSource",
            new XAttribute("Include", "Sources/invoice.txt"),
            new XAttribute("Bundle", "Reports/Invoice")
          )
        )
      )
    );
    (
      await consumer.DotnetAsync(
        "build",
        "--configuration",
        "Release",
        "-p:AtliTailwindStrict=true"
      )
    ).EnsureSuccess();
    var dynamicCss = consumer.ReadCss("DynamicReport");
    await Assert.That(dynamicCss).Contains(".bg-red-600{");
    await Assert.That(dynamicCss).Contains(".bg-blue-600{");
    await Assert.That(dynamicCss).Contains(".opacity-35{");
    await Assert.That(dynamicCss).Contains("owner candidate");
    await Assert.That(dynamicCss).DoesNotContain(".outline-double{");
    await Assert.That(consumer.ReadCss("Invoice")).Contains(".outline-double{");
    await Assert.That(consumer.ReadCss("Invoice")).DoesNotContain(".bg-red-600{");
    await Assert.That(consumer.ReadCss("Receipt")).DoesNotContain(".outline-double{");

    consumer.Write("Sources/dynamic.txt", "opacity-45");
    var staleSources = await consumer.DotnetAsync(
      "publish",
      "--configuration",
      "Release",
      "--no-build",
      "--output",
      consumer.PublishOutput
    );
    await Assert.That(staleSources.ExitCode).IsNotEqualTo(0);
    await Assert.That(staleSources.Output).Contains("dynamic.txt");
    await Assert.That(staleSources.Output.ToUpperInvariant()).Contains("BUILD");
    (
      await consumer.DotnetAsync(
        "build",
        "--configuration",
        "Release",
        "-p:AtliTailwindStrict=true"
      )
    ).EnsureSuccess();
    await Assert.That(consumer.ReadCss("DynamicReport")).Contains(".opacity-45{");
    await Assert.That(consumer.ReadCss("DynamicReport")).DoesNotContain(".opacity-35{");
    await Assert.That(consumer.ReadCss("DynamicReport")).DoesNotContain("owner candidate");
  }

  [Test]
  [Arguments("bundle")]
  [Arguments("scope")]
  [Arguments("policy")]
  public async Task Invalid_discovery_declarations_fail_with_configuration_guidance(string invalid)
  {
    var consumer = packages.CreateConsumer("DiscoveryConsumer");
    consumer.EditProject(project =>
    {
      var item =
        invalid == "policy"
          ? new XElement(
            "AtliTailwindExternal",
            new XAttribute("Include", "Vendor"),
            new XAttribute("Policy", "SelfStlyed")
          )
          : new XElement(
            "AtliTailwindComponent",
            new XAttribute("Include", "DiscoveryConsumer.Dynamic.RedStamp"),
            new XAttribute("Bundle", invalid == "bundle" ? "Reports/Typo" : "Reports/Invoice")
          );
      if (invalid == "scope")
      {
        item.SetAttributeValue("OwnerComponent", "DiscoveryConsumer.Reports.Invoice");
      }
      project.Add(new XElement("ItemGroup", item));
    });

    var result = await consumer.DotnetAsync("build", "--configuration", "Release");
    await Assert.That(result.ExitCode).IsNotEqualTo(0);
    await Assert
      .That(result.Output)
      .Contains(
        invalid switch
        {
          "bundle" => "Reports/Typo",
          "scope" => "OwnerComponent",
          _ => "Policy=SelfStyled",
        }
      );
  }

  [Test]
  public async Task Unmanifested_external_components_need_a_policy_in_strict_mode()
  {
    var consumer = packages.CreateConsumer("DiscoveryConsumer");
    var libraries = packages.CreateLibraries();
    libraries.Leaf.EditProject(project => project.Descendants("PackageReference").Remove());
    UseExternalInvoice(consumer, libraries.Outer);
    var unclassified = await consumer.DotnetAsync(
      "build",
      "--configuration",
      "Release",
      "-p:AtliTailwindStrict=true"
    );
    await Assert.That(unclassified.ExitCode).IsNotEqualTo(0);
    await Assert.That(unclassified.Output).Contains("Fixture.Leaf");
    consumer.EditProject(project =>
      project.Add(
        new XElement(
          "ItemGroup",
          new XElement(
            "AtliTailwindExternal",
            new XAttribute("Include", "Fixture.Leaf"),
            new XAttribute("Policy", "SelfStyled"),
            new XAttribute("Bundle", "Reports/Invoice")
          )
        )
      )
    );
    (
      await consumer.DotnetAsync(
        "build",
        "--configuration",
        "Release",
        "-p:AtliTailwindStrict=true"
      )
    ).EnsureSuccess();
    await Assert.That(consumer.ReadCss("Invoice")).Contains(".underline{");
    await Assert.That(consumer.ReadCss("Invoice")).DoesNotContain(".font-black{");

    consumer.EditProject(project =>
    {
      var input = project
        .Descendants("AtliTailwind")
        .Single(item => (string?)item.Attribute("Update") == "Reports/Invoice.tailwind.css");
      input.SetAttributeValue("RootComponent", "Fixture.Leaf.LeafHeading");
      input.SetAttributeValue("RootAssembly", "Fixture.Leaf");
    });
    var invalidRoot = await consumer.DotnetAsync("build", "--configuration", "Release");
    await Assert.That(invalidRoot.ExitCode).IsNotEqualTo(0);
    await Assert.That(invalidRoot.Output).Contains("LeafHeading");
    await Assert.That(invalidRoot.Output.ToUpperInvariant()).Contains("MANIFEST");
  }

  [Test]
  public async Task Exact_source_inputs_reuse_unaffected_bundles_and_explain_rebuilds()
  {
    var consumer = packages.CreateConsumer("DiscoveryConsumer");
    foreach (var report in new[] { "Invoice", "Receipt" })
    {
      consumer.Write($"Reports/{report}.tailwind.css", "@import \"tailwindcss\" source(none);\n");
    }

    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();
    var invoice = consumer.ReadCss("Invoice");
    var receipt = consumer.ReadCss("Receipt");
    var noop = await consumer.DotnetAsync(
      "build",
      "--configuration",
      "Release",
      "--no-restore",
      "--verbosity",
      "normal"
    );
    noop.EnsureSuccess();
    await Assert.That(noop.Output).Contains("Reports/Invoice unchanged (component graph)");
    await Assert.That(noop.Output).Contains("Reports/Receipt unchanged (component graph)");

    consumer.Replace("Shared/Unrelated.razor", "rotate-45", "rotate-90");
    var unrelated = await consumer.DotnetAsync(
      "build",
      "--configuration",
      "Release",
      "--no-restore",
      "--verbosity",
      "normal"
    );
    unrelated.EnsureSuccess();
    await Assert.That(unrelated.Output).Contains("Reports/Invoice unchanged (component graph)");
    await Assert.That(unrelated.Output).Contains("Reports/Receipt unchanged (component graph)");
    await Assert.That(consumer.ReadCss("Invoice")).IsEqualTo(invoice);
    await Assert.That(consumer.ReadCss("Receipt")).IsEqualTo(receipt);

    consumer.Replace("Reports/Invoice.razor.cs", "whitespace-nowrap", "whitespace-pre");
    var reachable = await consumer.DotnetAsync(
      "build",
      "--configuration",
      "Release",
      "--no-restore",
      "--verbosity",
      "normal"
    );
    reachable.EnsureSuccess();
    await Assert
      .That(reachable.Output)
      .DoesNotContain("Reports/Invoice unchanged (component graph)");
    await Assert.That(reachable.Output).Contains("Reports/Receipt unchanged (component graph)");
    await Assert.That(consumer.ReadCss("Invoice")).Contains(".whitespace-pre{");
    await Assert.That(consumer.ReadCss("Invoice")).DoesNotContain(".whitespace-nowrap{");
    await Assert.That(consumer.ReadCss("Receipt")).IsEqualTo(receipt);
    var explanations = Directory.GetFiles(
      consumer.Source("obj"),
      "*.explain.json",
      SearchOption.AllDirectories
    );
    using var invoiceExplain = JsonDocument.Parse(
      File.ReadAllText(
        explanations.Single(path => Path.GetFileName(path) == "Invoice.explain.json")
      )
    );
    using var receiptExplain = JsonDocument.Parse(
      File.ReadAllText(
        explanations.Single(path => Path.GetFileName(path) == "Receipt.explain.json")
      )
    );
    await Assert
      .That(invoiceExplain.RootElement.GetProperty("rebuildReason").GetString())
      .IsEqualTo("component, input, compiler or output changed");
    await Assert
      .That(receiptExplain.RootElement.GetProperty("rebuildReason").GetString())
      .IsEqualTo("unchanged");
    await Assert
      .That(invoiceExplain.RootElement.GetProperty("components").ToString())
      .Contains("DiscoveryConsumer.Shared.LineItem");
    await Assert
      .That(receiptExplain.RootElement.GetProperty("components").ToString())
      .DoesNotContain("DiscoveryConsumer.Shared.LineItem");
  }

  [Test]
  public async Task A_new_assembly_with_failed_css_cannot_publish_the_previous_bundle()
  {
    var consumer = packages.CreateConsumer("DiscoveryConsumer");
    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();
    consumer.Replace("Reports/Invoice.razor.cs", "whitespace-nowrap", "whitespace-pre");
    var failed = await consumer.DotnetAsync(
      "build",
      "--configuration",
      "Release",
      "-p:AtliTailwindExecutable=missing-compiler"
    );
    await Assert.That(failed.ExitCode).IsNotEqualTo(0);
    await Assert.That(failed.Output).Contains("missing-compiler");
    var stalePublish = await consumer.DotnetAsync(
      "publish",
      "--configuration",
      "Release",
      "--no-build",
      "--output",
      consumer.PublishOutput
    );
    await Assert.That(stalePublish.ExitCode).IsNotEqualTo(0);
    await Assert.That(stalePublish.Output.ToUpperInvariant()).Contains("BUILD");

    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();
    (
      await consumer.DotnetAsync(
        "publish",
        "--configuration",
        "Release",
        "--no-build",
        "--output",
        consumer.PublishOutput,
        "-p:AtliTailwindExecutable=missing-compiler"
      )
    ).EnsureSuccess();
    await Assert.That(consumer.ReadCss("Invoice", published: true)).Contains(".whitespace-pre{");
    await Assert
      .That(consumer.ReadCss("Invoice", published: true))
      .DoesNotContain(".whitespace-nowrap{");
  }

  [Test]
  [Category("Browser")]
  public async Task Nested_generic_dynamic_and_print_utilities_style_real_reports()
  {
    var consumer = packages.CreateConsumer("DiscoveryConsumer");
    EnableDynamicReport(consumer, declareAlternatives: true);
    (await consumer.DotnetAsync("build", "--configuration", "Release")).EnsureSuccess();
    foreach (var alternative in new[] { "--red", "--blue" })
    {
      var rendered = await consumer.RenderAsync(
        consumer.BuildOutput,
        pdf: true,
        "--dynamic",
        alternative
      );
      var invoice = await File.ReadAllBytesAsync(Path.Combine(rendered, "Invoice.pdf"));
      var dynamic = await File.ReadAllBytesAsync(Path.Combine(rendered, "DynamicReport.pdf"));
      await Assert.That(Encoding.ASCII.GetString(dynamic, 0, 5)).IsEqualTo("%PDF-");
      // The short document fits one page without the print:break-before-page utility.
      // Two PDF pages therefore prove the generated print rule was actually applied.
      await Assert.That(PdfPageObject().Count(Encoding.Latin1.GetString(invoice))).IsEqualTo(2);
    }
  }

  private static void EnableDynamicReport(ConsumerProject consumer, bool declareAlternatives)
  {
    consumer.Write("Reports/DynamicReport.tailwind.css", "@import \"tailwindcss\" source(none);\n");
    consumer.EditProject(project =>
      project.Add(
        new XElement(
          "ItemGroup",
          new XElement(
            "AtliTailwind",
            new XAttribute("Update", "Reports/DynamicReport.tailwind.css"),
            new XAttribute("RootComponent", "DiscoveryConsumer.Reports.DynamicReport")
          )
        )
      )
    );
    if (declareAlternatives)
    {
      DeclareDynamicAlternatives(consumer);
    }
  }

  private static void DeclareDynamicAlternatives(ConsumerProject consumer) =>
    consumer.EditProject(project =>
      project.Add(
        new XElement(
          "ItemGroup",
          StampNames.Select(type => new XElement(
            "AtliTailwindComponent",
            new XAttribute("Include", "DiscoveryConsumer.Dynamic." + type),
            new XAttribute("OwnerComponent", "DiscoveryConsumer.Reports.DynamicReport")
          ))
        )
      )
    );

  private static void UseExternalInvoice(ConsumerProject consumer, ConsumerProject outer)
  {
    consumer.EditProject(project =>
    {
      project.Add(
        new XElement(
          "ItemGroup",
          new XElement("ProjectReference", new XAttribute("Include", outer.Source("Outer.csproj")))
        )
      );
      var input = project
        .Descendants("AtliTailwind")
        .Single(item => (string?)item.Attribute("Update") == "Reports/Invoice.tailwind.css");
      input.SetAttributeValue("RootComponent", "Fixture.Outer.ExternalInvoice");
      input.SetAttributeValue("RootAssembly", "Fixture.Outer");
    });
    consumer.Replace(
      "Program.cs",
      "registry.AddReport<Invoice>",
      "registry.AddReport<Fixture.Outer.ExternalInvoice>"
    );
  }

  [GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])")]
  private static partial Regex PdfPageObject();
}
