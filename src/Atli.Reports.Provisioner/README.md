# atli-reports-provisioner

Creates, rolls out, and deletes the per-customer renderers of the
[hosted renderer service](../../docs/hosted-renderers.md) on Azure Container Apps Sandboxes, and
maintains the renderer records the gateway routes by. It runs off the request path, from an
operator's shell or a release pipeline, with rights the gateway never holds.

Each renderer is one sandbox that serves one tenant for its whole lifetime. The sandbox runs the
unmodified server image with Chromium's sandbox on, denies egress, admits only a credential that
belongs to that renderer alone, and suspends itself when idle. Its record holds the URL of its
port, its sandbox ID, and that credential. The gateway reads the record to route conversions and
resumes the sandbox when its proxy answers `403 {"error":"Sandbox is not running"}`.

## Azure roles

| Who | Role | Scope |
| --- | --- | --- |
| Provisioner | Container Apps SandboxGroup Data Owner | The renderer sandbox group |
| Provisioner, with `Records:Store` set to `KeyVault` | Key Vault Secrets Officer | The record vault |
| Gateway | Key Vault Secrets User | The record vault |
| Gateway | A custom role with only `Microsoft.App/sandboxGroups/sandboxes/read` and `Microsoft.App/sandboxGroups/sandboxes/resume/action` | The renderer sandbox group |

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
| `Provisioner:Sandboxes:SubscriptionId`, `ResourceGroup`, `SandboxGroup`, `Region` | Required | The renderer sandbox group |
| `Provisioner:Sandboxes:ManagedIdentityClientId` | Empty | A user-assigned identity for the data plane; empty for the default chain |
| `Provisioner:Records:Store` | Required | `KeyVault`, or `File` for development and tests |
| `Provisioner:Records:VaultUri` | | For `KeyVault`: the vault, such as `https://contoso.vault.azure.net/` |
| `Provisioner:Records:ManagedIdentityClientId` | Empty | For `KeyVault`: a user-assigned identity for the vault |
| `Provisioner:Records:Path` | | For `File`: the directory of record files |
| `Provisioner:DiskImageId` | | The disk image new renderers start from; `--disk-image` overrides it |
| `Provisioner:Size` | `M` | The size `create` uses: `S` (0.5 vCPU, 1 GiB, one conversion at a time), `M` (1 vCPU, 2 GiB, two), or `L` (2 vCPU, 4 GiB, four) |
| `Provisioner:AutoSuspendAfter` | `00:05:00` | Idle time after which the platform suspends a renderer |
| `Provisioner:ReadyTimeout` | `00:03:00` | How long a new renderer may take to answer `/health/ready` with `200` |
| `Provisioner:DrainDelay` | `00:02:30` | How long a rollout keeps a replaced renderer after its record moves; `--drain` overrides it |

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
    "DiskImageId": "<disk image ID>"
  }
}
```

The same settings as environment variables: `Provisioner__Sandboxes__Region=eastus2`,
`Provisioner__Records__Store=KeyVault`, and so on. Durations are `hh:mm:ss`.

## Commands

Every command accepts `--help`. Exit codes: `0` success, `1` failure, `2` a usage or configuration
error, found before anything changes. Progress lines start with the tenant, such as `[contoso]`.
No command ever prints a credential or a verifier.

### create

```bash
atli-reports-provisioner create --tenant contoso [--size S|M|L] [--disk-image <id>]
```

Creates a renderer for a tenant that has none:

1. Generates a credential for the renderer. Only its verifier goes into the sandbox's environment;
   the record keeps the credential for the gateway.
2. Creates the sandbox from the disk image at the size's CPU and memory, with the server image's
   entrypoint, egress denied, auto-suspend, and the labels `app=atli-reports`, `role=renderer`,
   `tenant=<id>`, and `size=<S|M|L>`.
3. Exposes port 8080 anonymously. The port URL is public; the renderer's credential check is the
   gate, as the [design](../../docs/hosted-renderers.md#azure-container-apps-sandboxes) explains.
4. Polls `GET <url>/health/ready` every 500 ms until it answers `200`, for up to `ReadyTimeout`.
   A new renderer answered in 1.4 to 5.2 seconds in the measured runs.
5. Writes the record, which makes the renderer routable.

If any step after the sandbox exists fails, or the command is canceled, the sandbox is deleted
again and no record is written. A tenant that already has a record is refused; `rollout` replaces
renderers. Tenant IDs are 1 to 63 lowercase letters, digits, and hyphens.

### rollout

```bash
atli-reports-provisioner rollout [--disk-image <id>] [--tenant <id>] [--max-parallel 4] [--drain 00:02:30]
```

Replaces every renderer whose record names another disk image, or only the given tenant's.
Suspended renderers are replaced too, without being resumed. For each one, the provisioner creates
a replacement exactly as `create` does, with a new credential and the old sandbox's `size` label
(`M` when it has none), waits until it is ready, and replaces the record in one step. The old
sandbox is deleted after the drain.

- `--max-parallel` bounds how many replacements are created at once. A drain does not hold a
  place, so a rollout takes about the tenant count divided by `--max-parallel`, times the time to
  ready, plus one drain.
- The drain must outlast the gateway's cached copy of the record (30 seconds) and its longest
  request (90 seconds). Until then, the gateway may still send conversions to the old renderer.
- A tenant that fails keeps its old renderer and record; a half-created replacement is deleted.
  The rollout carries on with the other tenants, prints a summary, and exits with `1`. Renderers
  already on the disk image are skipped, so running the same command again finishes the job.

### delete

```bash
atli-reports-provisioner delete --tenant contoso [--drain 00:02:30]
```

Deletes the tenant's record first, so the gateway stops routing to the renderer, then the sandbox.
Without `--drain` the sandbox goes at once, as it should for a compromised renderer; with it, the
command waits that long in between so conversions in flight can finish. It also deletes any other
sandbox labeled for the tenant, such as one an earlier, interrupted delete left behind, so running
it again is always safe.

### list

```bash
atli-reports-provisioner list
```

```text
TENANT    SANDBOX                               STATE    SIZE  DISK IMAGE  CREATED
contoso   3f0c1c4e-0d51-4f7a-9d6b-5f4bb0b1c2d3  Stopped  M     <disk id>   2026-10-03 21:40:12Z
fabrikam  8d1e7a90-4c55-4b8e-a1f2-0b9e6c3d4e5f  Running  L     <disk id>   2026-10-03 21:41:03Z
```

Lists each record with its sandbox's current state (`missing` when the sandbox is gone). Renderer
sandboxes that no record points to follow in a second table: one being created right now, or one
a failed or canceled command left behind. Delete a leftover with
`aca sandbox delete --id <sandbox ID> --yes`.

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
5. Run `list`: every renderer is on the new disk image, and no renderer sandbox lacks a record.

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
