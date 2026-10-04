# atli-reports-provisioner

Creates, rolls out, and deletes the per-customer renderers of the
[hosted renderer service](../../docs/hosted-renderers.md) on Azure Container Apps Sandboxes, and
maintains the renderer records the gateway routes by. It runs off the request path, from an
operator's shell or a release pipeline, with rights the gateway never holds.

Each renderer is one sandbox that serves one tenant for its whole lifetime. The sandbox runs the
unmodified server image with Chromium's sandbox on, denies egress, admits only a credential that
belongs to that renderer alone, and suspends itself when idle. Its record holds the URL of its
port, its sandbox ID, that credential, and how many requests the renderer admits at once. The
gateway reads the record to route conversions.

A suspended renderer is woken by the request itself: its port is exposed with on-demand
activation (`Provisioner:PortActivation`, `OnDemand` by default), so the platform's proxy resumes
the sandbox and then serves the request. Measured with the server image, an invoice came back in
about 0.6 to 1.5 seconds from a stopped renderer, and every request of a burst during the wake
succeeded. The gateway then needs no permission on the sandbox group. With `Manual` activation the
proxy answers `403 {"error":"Sandbox is not running"}` instead, and the gateway's
`Wake:Mode=Sandboxes` resumes the sandbox and retries; that remains the fallback.

An anonymous on-demand port lets anyone who learns its URL wake the renderer, and run up its
compute, before the renderer checks a credential. Set `Provisioner:AllowedSourceCidrs` to the
gateway's outbound addresses, so the platform's proxy refuses everyone else.

## Azure roles

| Who | Role | Scope |
| --- | --- | --- |
| Provisioner | Container Apps SandboxGroup Data Owner | The renderer sandbox group |
| Provisioner, with `Records:Store` set to `KeyVault` | Key Vault Secrets Officer | The record vault |
| Gateway | Key Vault Secrets User | The record vault |
| Gateway, only with `Wake:Mode=Sandboxes` (renderers with `Manual` ports) | A custom role with only `Microsoft.App/sandboxGroups/sandboxes/read` and `Microsoft.App/sandboxGroups/sandboxes/resume/action` | The renderer sandbox group |

Data Owner also allows running commands and reading files in every sandbox of the group, so
nothing on the request path may hold it. The gateway's custom role cannot create, delete, or
reconfigure renderers, run commands in them, or read their files. That role has not been tested
yet; see the [open questions](../../docs/hosted-renderers.md#open-questions-and-next-measurements).
The renderer sandbox group itself has no managed identity.

The provisioner authenticates as the user-assigned managed identity named by
`Sandboxes:ManagedIdentityClientId` (and `Records:ManagedIdentityClientId` for the vault), or
through the default chain when those are empty: a system-assigned identity in Azure, the Azure CLI
(`az login`) on a workstation. A new data-plane role assignment took from 17 seconds to over 13
minutes to take effect in the
[measured runs](../../benchmarks/results/2026-10-03-5b667b4-azure-sandboxes-amd64.md).

## Configuration

Settings come from `appsettings.json` next to the binary (optional), then environment variables
named `Provisioner__…`, then command-line flags; each source overrides the one before it.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Provisioner:Sandboxes:SubscriptionId`, `ResourceGroup`, `SandboxGroup`, `Region` | Required | The renderer sandbox group: a subscription GUID, Azure resource names, and a region name such as `eastus2` |
| `Provisioner:Sandboxes:ManagedIdentityClientId` | Empty | A user-assigned identity for the data plane; empty for the default chain |
| `Provisioner:Records:Store` | Required | `KeyVault`, or `File` for development and tests |
| `Provisioner:Records:VaultUri` | | For `KeyVault`: the vault, such as `https://contoso.vault.azure.net/` |
| `Provisioner:Records:ManagedIdentityClientId` | Empty | For `KeyVault`: a user-assigned identity for the vault |
| `Provisioner:Records:Path` | | For `File`: the directory of record files, which only its owner may write to |
| `Provisioner:DiskImageId` | | The disk image new renderers start from; `--disk-image` overrides it |
| `Provisioner:Size` | `M` | The size `create` uses: `S` (0.5 vCPU, 1 GiB, one conversion at a time), `M` (1 vCPU, 2 GiB, one), or `L` (2 vCPU, 4 GiB, two) |
| `Provisioner:PortActivation` | `OnDemand` | What a request to a suspended renderer does: `OnDemand` resumes it; `Manual` leaves that to the gateway |
| `Provisioner:AllowedSourceCidrs` | Empty | The source ranges a renderer's port admits, such as `["203.0.113.7/32"]`: the gateway's outbound addresses, and the provisioner's own; empty admits any. At most 100 |
| `Provisioner:AutoSuspendAfter` | `00:05:00` | Idle time after which the platform suspends a renderer; at most a day |
| `Provisioner:ReadyTimeout` | `00:03:00` | How long a new renderer may take to answer `/health/ready` with `200`; at most 15 minutes |
| `Provisioner:DrainDelay` | `00:02:30` | How long a rollout keeps a replaced renderer after its record moves, and prune waits before deleting; at most an hour; `--drain` overrides it |

```json
{
  "Provisioner": {
    "Sandboxes": {
      "SubscriptionId": "00000000-0000-0000-0000-000000000000",
      "ResourceGroup": "reports-renderers",
      "SandboxGroup": "reports-renderers",
      "Region": "eastus2"
    },
    "Records": {
      "Store": "KeyVault",
      "VaultUri": "https://reports-renderers.vault.azure.net/"
    },
    "DiskImageId": "<disk image ID>",
    "AllowedSourceCidrs": ["203.0.113.7/32", "203.0.113.8/32"]
  }
}
```

The same settings as environment variables: `Provisioner__Sandboxes__Region=eastus2`,
`Provisioner__Records__Store=KeyVault`, `Provisioner__AllowedSourceCidrs__0=203.0.113.7/32`, and so
on. Durations are `hh:mm:ss` everywhere: a bare number such as `DrainDelay=150` is refused, since
.NET would read it as 150 days. Every setting is checked before anything changes.

The sizes run about one conversion per vCPU: that is where throughput tops out in the sandboxes,
and each 49-page report in flight takes about 400 to 500 MiB, so a 2 GiB renderer running four to
eight at once was killed out of memory. Each renderer admits twice its conversions from the
gateway, the rest waiting in its queue, and its record says so, so the gateway sends no more.

`AllowedSourceCidrs` applies to every request to the port, the provisioner's own included: it asks
each new renderer whether it is ready through the port, so the address it runs from must be in the
list, or every `create` and `rollout` ends in the ready timeout with `HTTP 403`. A changed list
applies to renderers created from then on; `rollout` does not replace renderers on the current disk
image, so recreate them (`delete`, then `create`) to apply it to existing ones.

## Commands

Every command accepts `--help`. Exit codes: `0` success, `1` failure, `2` a usage or configuration
error, found before anything changes. Progress lines start with the tenant, such as `[contoso]`.
No command ever prints a credential or a verifier. The first Ctrl+C or `SIGTERM` cancels the
command and lets it delete what it created but has not recorded; a second one stops it at once.

### One command at a time

Run only one provisioner command per sandbox group at a time: in a release pipeline, put every job
that runs it in one concurrency group (a GitHub Actions `concurrency:` key, an Azure Pipelines
exclusive lock). Commands that overlap are still kept from corrupting records: just before a command
writes a tenant's record, it reads the record again, and if another command created, replaced, or
deleted it meanwhile, it discards its own new renderer and reports the tenant as failed. What can
remain is a sandbox no record points to, which `prune` deletes.

### create

```bash
atli-reports-provisioner create --tenant contoso [--size S|M|L] [--disk-image <id>]
```

Creates a renderer for a tenant that has none:

1. Generates a credential for the renderer. Only its verifier goes into the sandbox's environment;
   the record keeps the credential for the gateway.
2. Creates the sandbox from the disk image at the size's CPU and memory, with the server image's
   entrypoint, egress denied, auto-suspend, and the labels `app=atli-reports`, `role=renderer`,
   `tenant=<id>`, `size=<S|M|L>`, and `launch=<a new GUID>`. The renderer runs the size's
   conversions at once and queues as many more from the gateway, and accepts request bodies up to
   30 MiB: the gateway's JSON encoding of a body it admitted can grow it up to three times.
3. Exposes port 8080 anonymously, with `Provisioner:PortActivation`, and admitting only
   `Provisioner:AllowedSourceCidrs` when it is set. The port URL is public; the renderer's
   credential check is the gate, as the
   [design](../../docs/hosted-renderers.md#azure-container-apps-sandboxes) explains.
4. Polls `GET <url>/health/ready` every 500 ms until it answers `200`, for up to `ReadyTimeout`.
   A new renderer answered in 1.4 to 5.2 seconds in the measured runs.
5. Reads the tenant's record again, and stops if another command wrote one meanwhile.
6. Writes the record, which makes the renderer routable. It records how many requests the renderer
   admits at once (twice its conversions), which the gateway holds the tenant to.

If any step after the sandbox exists fails, or the command is canceled, the sandbox is deleted
again and no record is written. A create call that failed without an answer may still have made a
sandbox, so the provisioner then looks for its `launch` label and deletes what it finds. A record
write that failed may still have taken effect, so the provisioner reads the record back: if it
points to the new renderer, the create succeeded; if the read fails too, the sandbox is kept and
`prune` deletes it should no record point to it. Every cleanup runs even after a cancel, for at most
two minutes.

A tenant that already has a record, or one that cannot be read, is refused; `rollout` replaces
renderers. Tenant IDs are 1 to 63 lowercase letters, digits, and hyphens.

### rollout

```bash
atli-reports-provisioner rollout [--disk-image <id>] [--tenant <id>] [--max-parallel 4] [--drain 00:02:30]
```

Replaces every renderer whose record names another disk image or a sandbox that no longer exists,
or only the given tenant's. Suspended renderers are replaced too, without being resumed. For each
one, the provisioner creates a replacement exactly as `create` does, with a new credential and the
old sandbox's `size` label (`M` when it has none), waits until it is ready, and replaces the record
in one step. The old sandbox is deleted after the drain, if it is labeled for the tenant; a record
that names another tenant's sandbox is reported, and that sandbox is left alone.

- `--max-parallel` bounds how many replacements are created at once. A drain does not hold a
  place, so a rollout takes about the tenant count divided by `--max-parallel`, times the time to
  ready, plus one drain.
- The drain must outlast the gateway's cached copy of the record (30 seconds) and its longest
  request (90 seconds). Until then, the gateway may still send conversions to the old renderer.
- A tenant that fails keeps its old renderer and record; a half-created replacement is deleted.
  A record that cannot be read is a failure too, and its renderer is left alone. The rollout
  carries on with the other tenants, prints a summary, and exits with `1`. Renderers already on
  the disk image are skipped, so running the same command again finishes the job.
- Last, it prunes, as `prune` does (for the given tenant only, with `--tenant`): renderer sandboxes
  that an earlier, canceled or killed command left behind are deleted after one more drain. A
  canceled rollout skips this; its old sandboxes still draining are left to the next run.

### delete

```bash
atli-reports-provisioner delete --tenant contoso [--drain 00:02:30]
```

Deletes the tenant's record first, so the gateway stops routing to the renderer, then the sandbox.
A record that cannot be read, or that the vault holds disabled, is deleted all the same, so a
compromised renderer can always be cut off. Without `--drain` the sandbox goes at once, as it should
for a compromised renderer; with it, the command waits that long in between so conversions in
flight can finish. It deletes every sandbox labeled for the tenant, such as one an earlier,
interrupted delete left behind, so running it again is always safe. A sandbox the record names that
is labeled for another tenant is not deleted, and the command exits with `1`.

With the Key Vault store, deleting a record disables its current secret version before deleting the
secret. Creating the tenant again recovers the soft-deleted secret, which stays disabled, so no
record, until the new record is written; if that write fails, the secret is deleted again.

### list

```bash
atli-reports-provisioner list
```

```text
TENANT    SANDBOX                               STATE    SIZE  DISK IMAGE  CREATED
contoso   3f0c1c4e-0d51-4f7a-9d6b-5f4bb0b1c2d3  Stopped  M     <disk id>   2026-10-03 21:40:12Z
fabrikam  8d1e7a90-4c55-4b8e-a1f2-0b9e6c3d4e5f  Running  L     <disk id>   2026-10-03 21:41:03Z
```

Lists each record with its sandbox's current state (`missing` when the sandbox is gone; `rollout`
replaces it). Records the store holds but cannot read follow, with the reason, instead of failing
the list. Renderer sandboxes that no record points to follow in a last table: one being created
right now, or one a failed or canceled command left behind, which `prune` deletes.

### prune

```bash
atli-reports-provisioner prune [--tenant <id>] [--drain 00:02:30]
```

Deletes renderer sandboxes (labeled `app=atli-reports`, `role=renderer`, and a tenant) that no
record points to: the old sandboxes of a rollout canceled during its drain, the sandbox of a tenant
whose delete was interrupted, or one a killed create left behind. It keeps:

- every sandbox of a tenant whose record cannot be read, since that record may point to it;
- a sandbox created less than the drain and `ReadyTimeout` ago, or at an unknown time, which may
  belong to a create or rollout that has not written its record yet.

The others are deleted after the drain, in case the gateway still sends them work (a record may
have moved away from one moments ago), and only if no record points to them by then. A sandbox
that cannot be deleted is reported, and the command exits with `1`.

### disable and enable

```bash
atli-reports-provisioner disable --tenant contoso
atli-reports-provisioner enable --tenant contoso
```

`disable` disables every sandbox labeled for the tenant: the platform stops it and refuses to start
it again, whether a request reaches its on-demand port (`403`) or something resumes it
(`409 SandboxAdminDisabled`), until `enable`. The record and the sandbox's disk stay, for
investigation; the tenant's conversions fail meanwhile. Every sandbox is tried even when one
fails, and the command then exits with `1`. A sandbox the record names that is labeled for another
tenant is left alone. `enable` lets the tenant's sandboxes start again; they stay stopped until a
request or a resume starts them.

## Incident response

When a renderer may be compromised:

1. `disable --tenant <id>`: the kill switch. It takes effect at once, needs nothing from the
   gateway, and keeps the evidence.
2. Investigate with the disk and the platform's logs. Nothing on the request path can start the
   renderer meanwhile.
3. `delete --tenant <id>`: removes the record, which holds the renderer's credential, and the
   sandbox. Then `create --tenant <id>` makes a new renderer with a new credential. The old
   credential was the renderer's alone, so no other renderer admits it.
4. If it was a false alarm, `enable --tenant <id>` instead.

A record that cannot be read does not stop either command: `disable` and `delete` find the
tenant's sandboxes by their labels.

## Rolling out a server release

Every server image release replaces every renderer. A suspended renderer resumes with the browser
it was suspended with, so a security release reaches a customer only once that customer's renderer
has been recreated from the new image. The patch target is measured from the release to the end
of the rollout.

1. Build a disk image from the release's server Dockerfile in the renderer sandbox group
   (`aca sandboxgroup disk create --source <directory>`), and note its ID
   (`aca sandboxgroup disk list`).
2. Run `atli-reports-provisioner rollout --disk-image <id>`.
3. If it exits with `1`, fix what the summary reports and run the same command again.
4. Set `Provisioner:DiskImageId` to the new ID, so tenants created from now on start from it.
5. Run `list`: every renderer is on the new disk image, every record can be read, and no renderer
   sandbox lacks a record.

## Why one sandbox per customer, and no shared snapshot

A sandbox can be snapshotted with its memory and new sandboxes started from the snapshot, which
would skip the browser's start. The provisioner never does that across renderers. Sandboxes
started from one snapshot share the original's browser, libc, and stack addresses, which weakens
ASLR between customers, and its whole environment, credentials included; the
[measured runs](../../benchmarks/results/2026-10-03-5b667b4-azure-sandboxes-amd64.md#4-snapshots)
saw both. So each renderer starts from the disk image with its own credential, and a suspended
renderer is only ever resumed as itself. See
[Azure Container Apps Sandboxes](../../docs/hosted-renderers.md#azure-container-apps-sandboxes) in
the design.
