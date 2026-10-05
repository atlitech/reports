# Atli.Reports.Blazor.Tailwind.Discovery

Export build-time component manifests from a Razor component library. Applications using
`Atli.Reports.Blazor.Tailwind` can then follow the library's nested components without access to
its source files. This package does not download Tailwind or add report runtime dependencies.

```xml
<PackageReference Include="Atli.Reports.Blazor.Tailwind.Discovery" Version="0.26.0" PrivateAssets="all" />
```

Normal builds produce a manifest beside the implementation assembly. `dotnet pack` includes a
versioned manifest index and manifest under `atli-tailwind/v1/<tfm>/`. These are passive package
data: applications must explicitly opt into report graph compilation. Published applications
do not need the producer, its manifests, Roslyn, or the Tailwind compiler.

Discovery requires the .NET 10 SDK and portable or embedded Portable PDBs. It captures the actual
C# compiler arguments and runs an isolated analysis tool after compilation. A missing compiler
snapshot forces a normal recompilation. `pack --no-build` requires matching existing artifacts.

Declare dynamic alternatives or additional source text for a component in the library project:

```xml
<ItemGroup>
  <AtliTailwindComponent Include="Company.Reports.CompactAddress"
                        Assembly="Company.Reports"
                        OwnerComponent="Company.Reports.Invoice" />
  <AtliTailwindSource Include="Styles/InvoiceCandidates.txt"
                     OwnerComponent="Company.Reports.Invoice" />
</ItemGroup>
```

Complete utility names are required. Discovery cannot evaluate arbitrary runtime types or
constructed class names. Library themes, prebuilt styles, and isolated `.razor.css` output remain
application-managed. See the [application package guide](https://github.com/atlitech/reports/tree/main/src/Atli.Reports.Blazor.Tailwind)
for report roots, explicit styling policies, strict validation, and bundle compilation.
