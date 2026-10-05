# Migrating source integrations to 0.26

The renamed `Atli.Reports.*` packages first ship with 0.26.0. These changes also affect applications
and hosted deployments built from earlier commits of this repository.

## Report registration and JavaScript completion

All report registration now copies the global PDF defaults before applying a callback. Replace
options-object calls to `BlazorReportRegistry.AddReport<T>` with a callback:

```csharp
registry.AddReport<MyReport>(options =>
{
  options.ReportName = "custom";
  options.PdfOptions.Scale = 0.8;
});
```

`MapBlazorReport` and `RegisterBlazorReport` already use this shape. A registered report retains its
own settings; later changes to the global defaults or an options object retained by the callback
do not change it. Configure global defaults before registering reports.

`JavaScriptSettings`, `BlazorReportJavaScriptOptions`, and `DefaultJavaScriptSettings` are removed.
Use the engine's existing signal settings:

```csharp
app.MapBlazorReport<MyReport>(options =>
{
  options.PdfOptions.WaitForSignal = "reportReady";
  options.PdfOptions.WaitTimeout = TimeSpan.FromSeconds(10);
});
```

Report scripts can continue calling `blazorReport.completed()`. The template forwards it to the
configured signal. Leave `WaitForSignal` null for ordinary reports that print after loading and
font readiness. A per-report null value also disables an inherited signal. Invalid signal names
and negative timeouts other than `Timeout.InfiniteTimeSpan` are rejected at registration.

`BlazorReportBase` now supplies both `GlobalAssets` and `ReportAssets`, including through an
intermediate application base class. Remove a duplicate `ReportAssets` declaration from derived
components. Components that do not inherit this base can still declare their own parameter.

Blazor problem details now include `kind`. `PolicyDenied` returns 422. Upstream renderer credential
failures remain application failures (500), while signal timeouts retain the Blazor endpoint's 504
contract; the raw conversion server continues returning 422 for signal timeouts.

## Explicit rendering defaults

`PdfOptions.GenerateTaggedPdf` is now a nonnullable boolean that defaults to `true`. Set it to
`false` to request untagged output. Omitted or null HTTP request values resolve to `true`; the engine
always sends the resolved choice to Chromium. Replace source assignments of null with true.

Document networking defaults to `Disabled` in both the embedded engine and the server. Inline CSS,
scripts, and data URI assets continue working. For external assets, configure approved public
origins with `AllowList`. Trusted embedded reports that require unrestricted networking can opt in:

```csharp
services.AddReportsEngine(options =>
  options.Network.Mode = ReportsEngineNetworkMode.Unrestricted);
```

Set the policy on the process that performs conversion. A remote client's local engine settings do
not configure its server. See [document networking](security.md#document-networking).

## Hosted renderers

The tenant selector is always `X-Reports-Tenant`. Remove `ReportsServer:Gateway:TenantHeader` from
configuration and update any callers using an alternate header. Tenant membership checks remain
mandatory.

Gateway activation uses OnDemand renderer ports. Remove `ReportsServer:Gateway:Wake` and
`Provisioner:PortActivation`; the applications reject these obsolete settings with a migration
message. New provisioner-created ports always use OnDemand. Existing Manual ports must be migrated
before upgrading the gateway: changing application configuration does not alter an existing port.
Follow the port migration instructions in the [provisioner guide](../src/Atli.Reports.Provisioner/README.md#migrating-manual-renderer-ports).
Preserve port source-address restrictions and per-renderer credential isolation. Disabled renderers remain
disabled; enable/disable and tenant-deletion protections still apply. After migration, the gateway
no longer needs Azure sandbox read/resume permissions for activation.

File and KeyVault record-store names are case-insensitive across the gateway and provisioner.
KeyVault addresses must use HTTPS.

The internal Hosting assembly now requires current interface implementations: use the
`SandboxPortOptions` overload of `ISandboxesClient.AddPortAsync`, implement enable/disable directly,
and replace record-store `ListAsync` with either `ListTenantIdsAsync` or `ListWithUnreadableAsync`.
Tenant-ID listing must include unreadable records; full listing must report them explicitly.

## Development tooling

The example AppHost no longer provides the `Gotenberg:Enabled` switch. Use `benchmarks/run.sh` for comparisons;
the dedicated benchmark suite still manages the comparison image and workload settings. Historical
measurements remain unchanged. Runnable hosted-renderer scenarios use OnDemand activation.
