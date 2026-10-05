using Atli.Reports.Blazor.Tailwind.Contracts;

namespace Atli.Reports.Tailwind.Tests;

public class GraphContractTests
{
  [Test]
  public async Task Cycles_are_bounded_and_unreachable_components_do_not_contribute_candidates()
  {
    var manifest = Manifest(
      Component("Invoice", "uppercase", "Header"),
      Component("Header", "font-bold", "Invoice"),
      Component("Unrelated", "rotate-45")
    );

    var graph = new ReportGraphResolver([manifest]).Resolve(Request("Invoice"));

    await Assert.That(graph.Components.Count).IsEqualTo(2);
    await Assert.That(string.Join(" ", graph.CandidateFragments)).IsEqualTo("font-bold uppercase");
    await Assert.That(graph.Unresolved.Count).IsEqualTo(0);
    await Assert
      .That(graph.Components.Single(component => component.Identity == "App:Header").Path)
      .Contains("App:Invoice -> App:Header");
  }

  [Test]
  public async Task Owner_additions_follow_the_owner_while_bundle_additions_stay_in_one_report()
  {
    var directory = Path.Combine(Path.GetTempPath(), "atli-graph-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
      File.WriteAllText(Path.Combine(directory, "owned.txt"), "print:hidden");
      File.WriteAllText(Path.Combine(directory, "invoice.txt"), "before:content-['hello_world']");
      var manifest = Manifest(
        Component("Invoice", "uppercase", "Header"),
        Component("Receipt", "italic", "Header"),
        Component("Header", "font-bold"),
        Component("Stamp", "border-t-8"),
        Component("InvoiceExtra", "outline-double")
      );
      ManifestDeclarations.ApplyOwned(
        manifest,
        [new ComponentAddition { OwnerComponent = "Header", TypeName = "Stamp" }],
        [new SourceAddition { OwnerComponent = "Header", Path = "owned.txt" }],
        directory
      );
      var invoiceRequest = Request("Invoice");
      invoiceRequest.ProjectDirectory = directory;
      invoiceRequest.Components.Add(
        new ComponentAddition { Bundle = "Invoice", TypeName = "InvoiceExtra" }
      );
      invoiceRequest.Sources.Add(new SourceAddition { Bundle = "Invoice", Path = "invoice.txt" });
      var resolver = new ReportGraphResolver([manifest]);
      var invoice = resolver.Resolve(invoiceRequest);
      var receipt = resolver.Resolve(Request("Receipt"));

      foreach (var graph in new[] { invoice, receipt })
      {
        await Assert.That(graph.CandidateFragments).Contains("border-t-8");
        await Assert.That(graph.CandidateFragments).Contains("print:hidden");
      }

      await Assert.That(invoice.CandidateFragments).Contains("outline-double");
      await Assert.That(invoice.CandidateFragments).Contains("before:content-['hello_world']");
      await Assert.That(receipt.CandidateFragments).DoesNotContain("outline-double");
      await Assert
        .That(receipt.CandidateFragments)
        .DoesNotContain("before:content-['hello_world']");
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Test]
  public async Task External_styling_policies_are_scoped_and_do_not_hide_an_unclassified_library()
  {
    var component = Component("Invoice", "uppercase");
    component.Dependencies.Add(
      new ComponentReference { AssemblyName = "Vendor", TypeName = "Vendor.Chart" }
    );
    var resolver = new ReportGraphResolver([Manifest(component)]);
    var request = Request("Invoice");
    request.ExternalPolicies.Add(
      new ExternalComponentPolicy
      {
        AssemblyName = "Vendor",
        Policy = "SelfStyled",
        Bundle = "Invoice",
      }
    );

    var classified = resolver.Resolve(request);
    request.Bundle = "OtherInvoice";
    var unclassified = resolver.Resolve(request);

    await Assert.That(classified.Unresolved.Count).IsEqualTo(0);
    await Assert.That(classified.ExternalComponents.Count).IsEqualTo(1);
    await Assert.That(unclassified.Unresolved.Count).IsEqualTo(1);
    await Assert.That(unclassified.Unresolved[0].Message).Contains("Vendor.Chart");
    await Assert.That(unclassified.Unresolved[0].Message).Contains("App:Invoice");

    // An explicitly declared alternative promises discoverable candidates. A styling
    // exemption must not conceal a typo or missing manifest in that declaration.
    var declaredManifest = Manifest(component);
    ManifestDeclarations.ApplyOwned(
      declaredManifest,
      [
        new ComponentAddition
        {
          AssemblyName = "Vendor",
          TypeName = "Vendor.Chart",
          OwnerComponent = "Invoice",
        },
      ],
      [],
      Path.GetTempPath()
    );
    request.Bundle = "Invoice";
    await Assert
      .That(() => new ReportGraphResolver([declaredManifest]).Resolve(request))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Missing_roots_and_incomplete_advertised_manifests_fail_instead_of_becoming_empty_css()
  {
    var resolver = new ReportGraphResolver([
      Manifest(Component("Invoice", "uppercase", "Missing")),
    ]);
    await Assert
      .That(() => resolver.Resolve(Request("Invoice")))
      .Throws<InvalidOperationException>();
    await Assert
      .That(() => resolver.Resolve(Request("AbsentRoot")))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Unknown_manifest_contracts_and_invalid_owner_scopes_fail()
  {
    var manifest = Manifest(Component("Invoice", "uppercase"));
    manifest.SchemaVersion = "99.0";
    await Assert
      .That(() => new ReportGraphResolver([manifest]))
      .Throws<InvalidOperationException>();
    manifest.SchemaVersion = ComponentManifest.CurrentSchemaVersion;
    manifest.RequiredCapabilities = ["future-incompatible-capability"];
    await Assert
      .That(() => new ReportGraphResolver([manifest]))
      .Throws<InvalidOperationException>();
    manifest.RequiredCapabilities = [];
    await Assert
      .That(() =>
        ManifestDeclarations.ApplyOwned(
          manifest,
          [
            new ComponentAddition
            {
              TypeName = "Invoice",
              Bundle = "Invoice",
              OwnerComponent = "Invoice",
            },
          ],
          [],
          Path.GetTempPath()
        )
      )
      .Throws<InvalidOperationException>();
    await Assert
      .That(() =>
        ManifestDeclarations.ApplyOwned(
          manifest,
          [new ComponentAddition { TypeName = "Invoice", OwnerComponent = "Typo" }],
          [],
          Path.GetTempPath()
        )
      )
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Graph_fingerprints_ignore_unrelated_payload_changes_but_track_reachable_candidates()
  {
    var invoice = Component("Invoice", "uppercase");
    var unrelated = Component("Unrelated", "rotate-45");
    var manifest = Manifest(invoice, unrelated);
    var first = new ReportGraphResolver([manifest]).Resolve(Request("Invoice"));
    unrelated.CandidateFragments = ["skew-x-12"];
    var second = new ReportGraphResolver([manifest]).Resolve(Request("Invoice"));
    invoice.CandidateFragments = ["lowercase"];
    var third = new ReportGraphResolver([manifest]).Resolve(Request("Invoice"));

    await Assert.That(second.Fingerprint).IsEqualTo(first.Fingerprint);
    await Assert.That(third.Fingerprint).IsNotEqualTo(first.Fingerprint);
  }

  private static ComponentManifest Manifest(params ComponentEntry[] components) =>
    new()
    {
      Artifact = new ManifestArtifact
      {
        AssemblyName = "App",
        TargetFramework = "net10.0",
        ImplementationSha256 = new string('A', 64),
      },
      Components = [.. components],
    };

  private static ComponentEntry Component(
    string type,
    string candidates,
    params string[] children
  ) =>
    new()
    {
      TypeName = type,
      CandidateFragments = [candidates],
      Dependencies = children
        .Select(child => new ComponentReference { AssemblyName = "App", TypeName = child })
        .ToList(),
    };

  private static ReportGraphRequest Request(string root) =>
    new()
    {
      AssemblyName = "App",
      RootComponent = root,
      Bundle = root,
    };
}
