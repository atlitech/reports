# Atli.Reports.Blazor.Tailwind

Compile a separate Tailwind CSS v4 stylesheet for each Blazor report during `dotnet build` and
`dotnet publish`. Each report inlines its own CSS, so unrelated reports' utilities are left out.
The package uses the official standalone Tailwind CLI; Node and Bun are not required.

Part of [Atli Reports](https://github.com/atlitech/reports). The `Atli.Reports.*` packages first
ship with 0.26.0; until then, build and pack them from this repository.

## Install and register a report

```bash
dotnet add package Atli.Reports.Blazor.Tailwind
```

The package includes `Atli.Reports.Blazor`. Add `Reports/Invoice.tailwind.css` next to
`Reports/Invoice.razor`:

```css
@import "tailwindcss" source(none);
```

Declare the report's fully qualified component name in the project file:

```xml
<ItemGroup>
  <AtliTailwind Update="Reports/Invoice.tailwind.css"
                RootComponent="MyApp.Reports.Invoice" />
</ItemGroup>
```

The build follows static nested components and collects their rendered class candidates,
including supported generic components, templates, partial classes and class helpers owned by
the component. `source(none)` disables Tailwind's independent project-wide scan so unrelated
components do not contribute utilities.

Register the report with the input path relative to the project, without `.tailwind.css`:

```csharp
using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Tailwind;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBlazorReports();

var app = builder.Build();
app.MapBlazorReport<Invoice>(options => options.UseTailwind("Reports/Invoice"));
app.Run();
```

Each `*.tailwind.css` file creates one minified bundle. `Reports/Invoice.tailwind.css` becomes
`tailwind/Reports/Invoice.css` in the build and publish directories. `UseTailwind` resolves the
file relative to `AppContext.BaseDirectory`, independently of the process's working directory.
Use forward slashes in bundle paths, and keep their casing consistent across platforms.

`UseTailwind` also works on the global `AddBlazorReports` options for an intentionally shared
bundle. A report's own bundle overrides that global stylesheet; they are not concatenated.
Include shared styling in each report's input when needed.

## Component libraries and discovery boundaries

For a root in a referenced project or NuGet library, set `RootAssembly` as well:

```xml
<AtliTailwind Update="Reports/Invoice.tailwind.css"
              RootComponent="Company.Reports.Invoice"
              RootAssembly="Company.Reports" />
```

The library author adds the build-only `Atli.Reports.Blazor.Tailwind.Discovery` package with
`PrivateAssets="all"`. It exports a manifest with separate entries for each component. Nested
project and NuGet dependencies are resolved from the build's selected libraries, without shipping
their source or executing component code. See the
[producer guide](https://github.com/atlitech/reports/tree/main/src/Atli.Reports.Blazor.Tailwind.Discovery).

Automatic discovery requires a normal C# Razor build with portable or embedded Portable PDBs.
Keep `DebugType` set to `portable` (the SDK default) or `embedded` in projects producing manifests.
The PDB and compiler snapshot are build inputs; published applications need neither. A missing
or stale snapshot triggers normal recompilation. Incompatible manifests or implementations fail
with a rebuild remedy.

Ordinary static children and supported inline fragments are followed recursively. All known
conditional alternatives are included. Runtime-selected types and external fragments may require
explicit possible components; arbitrary data-driven class strings still need complete candidates.
This is a conservative stylesheet for the report's supported and declared states.

Declare possible runtime-selected components for a single bundle:

```xml
<AtliTailwindComponent Include="MyApp.Reports.CompactAddress" Bundle="Reports/Invoice" />
<AtliTailwindComponent Include="MyApp.Reports.FullAddress" Bundle="Reports/Invoice" />
```

Use `Assembly="Company.Reports"` for additions from another assembly. An addition must name a
component with a usable manifest. These declarations include CSS; they do not restrict which
types your application can render. Library authors can instead use `OwnerComponent` to attach
additions to a library component, as described in the producer guide.
The list asserts coverage of runtime component choices throughout that bundle or owner scope;
include every possible alternative used by those sites.

Unresolved component coverage warns by default. Enable strict mode for an error instead:

```xml
<PropertyGroup>
  <AtliTailwindStrict>true</AtliTailwindStrict>
</PropertyGroup>
```

For an ordinary third-party component whose styling you supply separately, declare that policy:

```xml
<AtliTailwindExternal Include="Vendor.Components" Policy="SelfStyled" Bundle="Reports/Invoice" />
```

An optional `Component` metadata value narrows the policy to one fully qualified type. It does
not import CSS; add the required styles to your input explicitly. A missing selected root or
broken advertised manifest is always an error. Blazor's `.razor.css` isolation and library static
assets remain separate from utility discovery.

## Themes and additional class sources

Share a theme across bundles with a CSS import:

```css
@import "tailwindcss" source(none);
@import "../Styles/theme.css";
```

`Styles/theme.css`:

```css
@theme {
  --color-brand: #334155;
}
```

Use complete class names in conditional expressions, such as `"text-red-600"` and
`"text-green-600"`. A constructed string such as `$"text-{color}-600"` cannot be detected.
For known classes supplied by data, include an explicit safelist in the input:

```css
@source inline("text-red-600 text-green-600");
```

Add explicit source files for class helpers the graph cannot associate with a component:

```css
@source "../Formatting/report-classes.txt";
```

Paths are relative to the authored input stylesheet. Broad explicit sources intentionally
broaden the bundle. The application controls theme and custom CSS imports; discovery does not
prune arbitrary authored CSS or merge library themes automatically.

To keep manual source mode, omit `RootComponent` and list the report and descendants explicitly:

```css
@import "tailwindcss" source(none);
@source "./Invoice.razor";
@source "./Invoice.razor.cs";
@source "../Shared/ReportHeader.razor";
```

Each bundle covers the report's possible declared styles, not only the state shown by one set
of report data. Tailwind's base styles and required theme variables are included in each
standalone document. Separate bundles reduce HTML payloads; PDF sizes do not necessarily shrink
by the same amount.

See Tailwind's [source detection](https://tailwindcss.com/docs/detecting-classes-in-source-files)
and [theme documentation](https://tailwindcss.com/docs/theme).

## Build and publish

```bash
dotnet build
dotnet publish -c Release
```

The build integration runs under the .NET 10 SDK's MSBuild. Its first compilation downloads
Tailwind **4.3.3** from the official GitHub release and caches the compiler. Subsequent builds
reuse it. The compiler is a build dependency; published applications need neither Tailwind nor
Node installed. No compiler process starts while rendering a report.

Automatic downloads support Windows x64, macOS x64/arm64, and Linux x64/arm64 (glibc or musl).
The compiler runs on the build host, including when publishing for another platform. Other build
hosts need a compatible executable supplied through `AtliTailwindExecutable`.

Discovery manifests are cached by compiler and producer input fingerprints. Graph inputs
containing only `@import "tailwindcss" source(none);` can also reuse unchanged CSS when reachable
candidates, compiler settings and output hashes match. Manual inputs and inputs with additional
CSS/import/source directives compile conservatively, preserving changes to external dependencies
and removals. Unchanged CSS bytes retain their timestamps.

Design-time builds do not run discovery, compile CSS or download tools. `dotnet publish --no-build`
validates and uses a successful build with the same configuration and target framework. It does
not invoke discovery or Tailwind, and refuses mismatched assemblies, changed graph configuration
or partially updated CSS. Named `AtliTailwindSource` contents and the restored dependency graph
must still match. The current strictness policy also applies to saved coverage diagnostics.
The executable override need not exist for `--no-build`; only a normal build invokes it.

The build supports a pinned version and an existing executable for offline or controlled builds:

```xml
<PropertyGroup>
  <AtliTailwindVersion>4.3.3</AtliTailwindVersion>
  <AtliTailwindExecutable>/tools/tailwindcss</AtliTailwindExecutable>
</PropertyGroup>
```

The executable override bypasses downloading; provide a compatible Tailwind v4 standalone CLI.
The first automatic download needs access to GitHub release assets. For an offline build, restore
NuGet dependencies and provision the compiler beforehand.

| Property | Default | Purpose |
| --- | --- | --- |
| `AtliTailwindEnabled` | `true` | Enable the package's compilation and generated asset integration. |
| `EnableDefaultAtliTailwindItems` | `true` | Discover `**/*.tailwind.css`, excluding build output. |
| `AtliTailwindVersion` | `4.3.3` | Exact standalone compiler release. |
| `AtliTailwindExecutable` | Automatic | Use an executable file path, absolute or relative to the project, instead of downloading one. |
| `AtliTailwindCacheDirectory` | `LocalApplicationData/Atli.Reports/Tailwind` | Override where downloaded compilers are stored. |
| `AtliTailwindMinify` | `true` | Minify generated bundles. |
| `AtliTailwindTimeoutSeconds` | `120` | Bound each compiler invocation and download/cache wait. |
| `AtliTailwindDiscoveryTimeoutSeconds` | `120` | Bound the component analysis process. |
| `AtliTailwindStrict` | `false` | Treat unresolved component coverage as an error. |

For consumers already compiling CSS through npm, Bun, or another pipeline, continue using
`BaseStylesPath` directly. No Tailwind package is needed for that workflow.

## External report projects and custom bundle names

Inputs outside the consuming project must be declared explicitly with a bundle path:

```xml
<ItemGroup>
  <AtliTailwind Include="../SharedReports/Invoice.tailwind.css" BundlePath="Invoices/Standard" />
</ItemGroup>
```

Register that output with `options.UseTailwind("Invoices/Standard")`. Source paths inside the
external CSS remain relative to that CSS file. To rename an automatically discovered input,
use `<AtliTailwind Update="Reports/Invoice.tailwind.css" BundlePath="Invoices/Standard" />`.
Bundle names must be unique, including when compared without regard to case.

To declare all inputs explicitly, set `EnableDefaultAtliTailwindItems` to `false` and add an
`AtliTailwind` item for each input. Add the package directly to the project that builds and hosts
the reports; its build targets do not flow into unrelated consumers transitively.

## Development

Run a normal rebuild after changing source classes or CSS. For automatic rebuilds and restarts:

```bash
dotnet watch --no-hot-reload
```

Report CSS is cached by default. If another build updates the compiled output while the app is
running, opt into rereading that report's CSS on every render:

```csharp
app.MapBlazorReport<Invoice>(options => options.UseTailwind(
  "Reports/Invoice",
  reloadOnChange: app.Environment.IsDevelopment()));
```

This option reloads the generated CSS; it does not run a compiler or start a file watcher. Keep
it disabled in production to reuse the loaded stylesheet. Shared sources outside the project
may need explicit `Watch` items for `dotnet watch` to trigger a build:

```xml
<ItemGroup>
  <Watch Include="../SharedReports/**/*.razor;../SharedReports/**/*.cs;../SharedReports/**/*.css" />
</ItemGroup>
```

After building a graph-mode report, locate its explanation with:

```bash
dotnet msbuild -t:AtliTailwindExplain
```

Each `graph/<bundle>.explain.json` beneath the Tailwind intermediate directory records the root,
component dependency paths, external policies, unresolved coverage and rebuild reason. Generated
candidate text beside it helps diagnose missing utilities. These files are not deployed.

The [TailwindReportServer example](https://github.com/atlitech/reports/tree/main/examples/TailwindReportServer)
shows two separate bundles automatically discovering a shared Razor header from another project.
It imports the source build
assets explicitly because it uses project references; NuGet consumers need only the package
reference.
