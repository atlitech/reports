# Atli.Reports.Blazor.Components

Razor components for
[Atli.Reports.Blazor](https://www.nuget.org/packages/Atli.Reports.Blazor) reports:

- `BlazorReportsBase`: a base class for report components. Inherit it to receive the files of the
  configured assets folder as `data:` URIs in `GlobalAssets`, keyed by file name.
- `BlazorReportsTemplate`: the HTML document every report renders into. It inlines the base
  styles and defines `blazorReport.completed()` for reports that wait for their JavaScript.

```razor
@inherits Atli.Reports.Blazor.Components.BlazorReportsBase

<img src="@GlobalAssets.GetValueOrDefault("logo.png")" alt="Logo" />
```

Apps install `Atli.Reports.Blazor`, which depends on this package. Reference this package
directly from a Razor class library that holds report components and does not need the rest of
Atli.Reports.Blazor.

Formerly **BlazorReports.Components**; see the
[migration guide](https://github.com/atlitech/reports/blob/main/docs/migration/from-blazorreports.md).
Part of [Atli Reports](https://github.com/atlitech/reports).
