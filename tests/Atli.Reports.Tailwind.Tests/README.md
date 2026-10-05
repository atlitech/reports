# Tailwind package acceptance tests

These tests pack the real NuGet packages into a temporary feed and create Razor applications
outside this repository, with an isolated NuGet cache. They exercise the package as a consumer
receives it, including the official pinned Tailwind standalone compiler. The first run needs
network access to NuGet and the Tailwind GitHub release.

Run the build and publish cases without a browser:

```shell
dotnet test --project tests/Atli.Reports.Tailwind.Tests/Atli.Reports.Tailwind.Tests.csproj --treenode-filter '/*/*/*/*[Category!=Browser]'
```

Run every case on a machine with Chrome or Chromium installed:

```shell
dotnet test --project tests/Atli.Reports.Tailwind.Tests/Atli.Reports.Tailwind.Tests.csproj
```

The browser test renders through the real reports engine. Its report signals completion only
when `getComputedStyle` confirms the report width, underline, and shared component's font
weight. A valid PDF without those styles cannot pass.

The discovery corpus also covers a nested shared header, an inferred generic component with
templated children, code-behind and inherited candidates, arbitrary values, and two declared
dynamic alternatives. Its browser test checks computed nested styles and verifies that a
`print:break-before-page` utility creates the expected second PDF page.

The other tests check separate per-report utilities, explicit shared Razor sources, shared
themes, CSS changes and removals, stale bundle removal, output path collisions, clean publish,
`publish --no-build`, and HTML rendering from another working directory without the compiler
cache. Build outputs are isolated from normal repository artifacts. No Docker, Node.js, or Bun
is required.

Discovery cases additionally compare project-reference CSS with CSS produced from transitive
NuGet libraries after deleting their original sources. They exercise strict diagnostics,
owner/bundle-scoped additions, external styling policies, changed source membership, and a
failed CSS build followed by `publish --no-build`. The original manual-source cases remain
separate regression tests. Exact standalone source inputs also check that no-op and unrelated
component edits reuse both bundles, while a reachable edit rebuilds only its report and records
the cause in its explanation file. Producer tests inspect platform-normalized multi-framework
NuGet indexes, verify packed assembly hashes, and reject missing manifests during `pack --no-build`.

Run only the fast manifest/graph contract tests, without packing or downloading the compiler:

```shell
dotnet test --project tests/Atli.Reports.Tailwind.Tests/Atli.Reports.Tailwind.Tests.csproj --treenode-filter '/*/*/GraphContractTests/*'
```
