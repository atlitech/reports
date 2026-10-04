# atli-reports-provisioner

Creates, rolls out, and deletes the per-customer renderers of the
[hosted renderer service](../../docs/hosted-renderers.md) on Azure Container Apps Sandboxes, and
maintains the renderer records the gateway routes by. It runs off the request path, from an
operator's shell or a release pipeline, or as the
[provisioning service](#the-provisioning-service) that creates renderers on demand, with rights the
gateway never holds.

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
gateway's outbound addresses, so the platform's proxy refuses everyone else. A gateway running as
a Container App on a subnet with a NAT gateway reaches the ports from the NAT gateway's public
address, so its `/32` is the gateway's entry.

## Azure roles

| Who | Role | Scope |
| --- | --- | --- |
| Provisioner | Container Apps SandboxGroup Data Owner | The renderer sandbox group |
| Provisioner, with `Records:Store` set to `KeyVault` | Key Vault Secrets Officer | The record vault |
| [Provisioning service](#the-provisioning-service) (`serve`), an identity of its own | The provisioner's two roles: Container Apps SandboxGroup Data Owner, and Key Vault Secrets Officer with a `KeyVault` store | The renderer sandbox group, and the record vault |
| Gateway | Key Vault Secrets User | The record vault |
| Gateway, only with `Wake:Mode=Sandboxes` (renderers with `Manual` ports) | A custom role with only `Microsoft.App/sandboxGroups/sandboxes/read` and `Microsoft.App/sandboxGroups/sandboxes/resume/action` | The renderer sandbox group |

Data Owner also allows running commands and reading files in every sandbox of the group, so
nothing on the request path may hold it. The gateway's custom role cannot create, delete, or
reconfigure renderers, run commands in them, or read their files. In the
[production-shaped run](../../benchmarks/results/2026-10-04-6cdce25-hosted-renderers-production-amd64.md),
the gateway's user-assigned identity, holding only that role, the vault role, and `AcrPull` on its
registry, resumed every `Manual` renderer it was asked to. The renderer sandbox group itself has no
managed identity.

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
| `Provisioner:Records:Store` | Required | `KeyVault`, or `File` for development and tests. One store per renderer sandbox group: every record in it counts as a renderer of this group, so a store shared with another group shows that group's tenants as `missing`, and a `rollout` without `--tenant` recreates them here |
| `Provisioner:Records:VaultUri` | | For `KeyVault`: the vault, such as `https://contoso.vault.azure.net/` |
| `Provisioner:Records:ManagedIdentityClientId` | Empty | For `KeyVault`: a user-assigned identity for the vault |
| `Provisioner:Records:Path` | | For `File`: the directory of record files, which only its owner may write to |
| `Provisioner:DiskImageId` | | The disk image new renderers start from; `--disk-image` overrides it |
| `Provisioner:Size` | `M` | The size `create` uses: `S` (0.5 vCPU, 1 GiB, one conversion at a time), `M` (1 vCPU, 2 GiB, one), or `L` (2 vCPU, 4 GiB, two) |
| `Provisioner:PortActivation` | `OnDemand` | What a request to a suspended renderer does: `OnDemand` resumes it; `Manual` leaves that to the gateway |
| `Provisioner:AllowedSourceCidrs` | Empty | The source ranges a renderer's port admits, such as `["203.0.113.7/32"]`: the gateway's outbound addresses, and the provisioner's own; empty admits any. At most 100 |
| `Provisioner:NetworkConnection` | Empty | The renderer group's virtual network connection (`aca sandboxgroup network create --name`) new renderers start in; empty for none. A network whose security group denies the `AzurePlatformDNS` service tag leaves renderers without DNS; see the [design](../../docs/hosted-renderers.md#azure-container-apps-sandboxes). Renderers in such a network were ready 1.4 to 3.5 s after the create call started, against 1.2 to 1.3 s without one |
| `Provisioner:AutoSuspendAfter` | `00:05:00` | Idle time after which the platform suspends a renderer; at most a day |
| `Provisioner:ReadyTimeout` | `00:03:00` | How long a new renderer may take to answer `/health/ready` with `200`; at most 15 minutes |
| `Provisioner:DrainDelay` | `00:02:30` | How long a rollout keeps a replaced renderer after its record moves, and prune waits before deleting; at most an hour; `--drain` overrides it |
| `Provisioner:Service:TenantPrefixes` | Required for `serve`, `retire`, and `rollout --stopped retire` | The prefixes whose tenants' renderers the [provisioning service](#the-provisioning-service) creates on demand and retires when idle. Each has a `Prefix`, such as `myapp-` (2 to 27 lowercase letters, digits, and hyphens, ending with a hyphen; no prefix may start with another); `MaxTenants`, the most renderers under it (`1000` by default, at most 100000); and `Size`, `S`, `M`, or `L`, or empty for `Provisioner:Size` |
| `Provisioner:Service:ApiKeys` | Required for `serve` | The gateway's keys for the service: each an `Id`, the part of the key before the dot, and a `Hash`, the base64 SHA-256 of the whole key. Two allow rotation |
| `Provisioner:Service:MaxCreatesPerMinute` | `60` | Renderers `serve` creates per minute at most, across all prefixes, failed creates included; at most 10000 |
| `Provisioner:Service:RetireAfterIdle` | `7.00:00:00` | How long a managed tenant's renderer may stay stopped before it is retired; `00:00:00` never retires, and otherwise at least a minute and at most 365 days |
| `Provisioner:Service:RetireCheckInterval` | `01:00:00` | How long `serve` waits after starting, and after each retirement run, before the next; a minute to a day |

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
image, so recreate them (`delete`, then `create`) to apply it to existing ones. The same holds for
`PortActivation` and `NetworkConnection`, and a rollout's replacements take the settings it runs with, not their
predecessors': a tenant created with a one-off `Manual` override comes back `OnDemand` unless the
rollout runs with the same override.

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
                                 [--stopped replace|retire]
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
- `--stopped retire` retires, instead of replacing, the stopped renderers of tenants under
  `Provisioner:Service:TenantPrefixes`, however long they have been stopped: each is deleted with
  its record, as `retire` does, and the tenant's next conversion has the provisioning service
  create a renderer from its disk image. Running and disabled renderers, records whose sandbox is
  gone, and tenants outside the prefixes are replaced as without it; with `--tenant`, the same rule
  applies to that tenant. Just before retiring a renderer, the rollout reads its record and sandbox
  again: a renderer that has started meanwhile is replaced instead, and a record another command
  changed is a failure. It needs valid `TenantPrefixes`, or it is a configuration error. The
  default, `--stopped replace`, replaces stopped renderers like the others.

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

### retire

```bash
atli-reports-provisioner retire
```

Retires, once, the renderers of tenants under `Provisioner:Service:TenantPrefixes` that have been
stopped for longer than `Provisioner:Service:RetireAfterIdle`; `serve` does the same every
`RetireCheckInterval`. Retiring a renderer deletes its record, then every sandbox labeled for the
tenant, as `delete` does, and with the sandbox its memory snapshot. The tenant's next conversion
has the provisioning service create a renderer from its `DiskImageId`, so a workspace nobody uses
costs nothing, and comes back on the current release.

A renderer is retired when its sandbox is stopped, not disabled, and the data plane reports that
it stopped more than `RetireAfterIdle` ago. A record whose sandbox no longer exists is retired too,
and its progress line says so. It keeps:

- running renderers, and disabled ones (`disable` keeps a renderer's disk for investigation);
- a stopped renderer whose stop time the data plane does not report;
- every sandbox of a tenant whose record cannot be read;
- every tenant outside the prefixes, such as the operator's own.

A record that names a sandbox labeled for another tenant is kept and reported as a failure. Just
before deleting, `retire` reads the tenant's record and sandbox again, and keeps the renderer until
the next run if another command changed the record meanwhile, or if the sandbox is no longer
stopped (a request may be waking it) or has been disabled. That leaves a short window, between
the second read and the delete, in which a conversion that wakes the renderer can still fail.

With `RetireAfterIdle` at `00:00:00` it retires nothing, and says so. It needs valid
`TenantPrefixes`, or it is a configuration error, but no disk image. A tenant that cannot be retired is reported, the others
are still retired, and the command exits with `1`; a sandbox left behind after its record was
deleted is deleted by `prune`.

The gateway keeps a record for `ReportsServer:Gateway:Records:CacheDuration` (30 seconds by
default), so for up to that long after a retirement it may still route the tenant's conversions to
the retired renderer. The platform's proxy answers them with `404`, as for any sandbox that no
longer exists, and with on-demand provisioning the gateway then has the provisioning service create
the renderer again: the conversion succeeds, only slower, since it waits for the new renderer.

### disable and enable

```bash
atli-reports-provisioner disable --tenant contoso
atli-reports-provisioner enable --tenant contoso
```

`disable` disables every sandbox labeled for the tenant: the platform stops it and refuses to start
it again, whether a request reaches its on-demand port (`403`) or something resumes it
(`409 SandboxAdminDisabled`), until `enable`. The record and the sandbox's disk stay, for
investigation; the tenant's conversions fail meanwhile, with `503` from the gateway at once,
whether its `Wake:Mode` is `None` or `Sandboxes`. Every sandbox is tried even when one
fails, and the command then exits with `1`. A sandbox the record names that is labeled for another
tenant is left alone. `enable` lets the tenant's sandboxes start again; they stay stopped until a
request or a resume starts them.

## The provisioning service

```bash
atli-reports-provisioner serve
```

`serve` runs the provisioner as a service of its own: an HTTP API that the gateway calls to create
a renderer for a tenant under one of `Provisioner:Service:TenantPrefixes` on the tenant's first
conversion, and to delete one when the application deletes the tenant. It holds the provisioner's
roles, which the gateway never does, and serves only the managed prefixes within their limits. A
compromised gateway can therefore create renderers under those prefixes and delete their tenants,
but cannot change, reassign, or enter a renderer, nor touch a tenant outside the prefixes. See
[Applications with many tenants](../../docs/hosted-renderers.md#applications-with-many-tenants) in
the design.

Every request but the health probes carries one of the gateway's keys in `X-Reports-Api-Key`.

| Request | Answer |
| --- | --- |
| `PUT /tenants/{id}/renderer` | `200` with `{"tenantId": "<id>", "created": false}` when the tenant's record names a sandbox that exists, disabled or not: a disabled renderer stays disabled. Otherwise, once a renderer is ready, `200` with `"created": true`: the service creates one as `create` does for a tenant without a record, or replaces the record's missing sandbox as `rollout` does, deleting nothing else |
| `DELETE /tenants/{id}/renderer` | `204` once the tenant's record, then every sandbox labeled for it, are deleted, as `delete` without `--drain` does; also when there was nothing to delete |
| `GET /health/live` | `200` while the service answers; no key needed |
| `GET /health/ready` | `200` while the record store answers, `503` otherwise; no key needed |

Failures are [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details with a `kind`:

| Status | `kind` | When |
| --- | --- | --- |
| `400` | `InvalidRequest` | The tenant ID is not one; also an unknown route (`404`) or method (`405`) |
| `401` | `Unauthorized` | No key, or not one of `Provisioner:Service:ApiKeys` |
| `403` | `NotAllowed` | The tenant is under none of the prefixes, which also means that the gateway's `TenantPrefixes` and the service's disagree |
| `429` | `QuotaExceeded` | A new renderer would put more than `MaxTenants` under the tenant's prefix. No `Retry-After`: it lasts until tenants under the prefix are deleted or retired |
| `429` | `RateLimited` | `MaxCreatesPerMinute` creates started in the last minute. `Retry-After` says in how many seconds one may start |
| `503` | `Failed` | The renderer could not be created (the data plane, the record store, or its readiness failed), the delete failed, the tenant's record cannot be read, or the service is stopping. The log says why; the answer never does |

Creating a renderer on demand:

- A tenant whose renderer exists is answered at once, and counts toward no limit.
- Concurrent requests for one tenant share one creation and its answer. A request that gives up,
  such as one past the gateway's `Provisioning:Timeout`, leaves the creation running, and the next
  request finds its renderer.
- The quota counts the tenants under the prefix that have a record, and the creations in flight.
  The service lists the store's tenants (with Key Vault, the secret names alone) at most once
  every 30 seconds, and counts its own creates and deletes since on top; a tenant created or
  deleted from the command line or by another replica counts from the next listing. Replacing a
  missing sandbox adds no renderer, so the quota never refuses it.
- The rate limit counts every create started in the last minute, across all prefixes, failed ones
  included. Finding a renderer is never limited.
- The new renderer has the prefix's `Size`, or `Provisioner:Size`, and starts from
  `Provisioner:DiskImageId`, which `serve` requires; every other setting applies as for `create`.
- A `DELETE` while the tenant's renderer is being created waits for the creation to finish first,
  so that it cannot write its record after the delete.

With `RetireAfterIdle` above zero, `serve` also retires idle renderers as `retire` does: the first
time `RetireCheckInterval` after it starts, then `RetireCheckInterval` after each run ends. A run
that fails is logged, and the next one runs as planned. With `RetireAfterIdle` at `00:00:00` it
retires nothing.

### Keys

Generate a key as for the server, with `scripts/create-reports-api-key.sh`. Give the service the
`Id` and `Hash` from its `server.env`, as `Provisioner__Service__ApiKeys__0__Id` and
`Provisioner__Service__ApiKeys__0__Hash`, and give the gateway the credential from `client.env`, as
`ReportsServer__Gateway__Provisioning__ApiKey`; the rest of `server.env` does not apply. The service
compares the SHA-256 of the whole key with the `Hash` of the key its ID names, in constant time. To
rotate, add the new key as `ApiKeys__1`, move the gateway to it, then remove the old one.

### Deployment

- **Internal ingress, TLS at the platform.** The service listens on plain HTTP, where
  `ASPNETCORE_URLS` or `ASPNETCORE_HTTP_PORTS` says, and on `http://+:8080` when neither is set.
  Expose it on internal ingress only, behind TLS that the platform terminates; the gateway's
  `Provisioning:Url` is its `https` address. Nothing else should reach it.
- **An identity of its own**, with the roles in [Azure roles](#azure-roles). Data Owner stays off
  the request path: the gateway reaches the service only through this API, which can neither run
  commands in a renderer nor read its files. The service asks each new renderer whether it is ready
  through the renderer's port, so its outbound address belongs in `Provisioner:AllowedSourceCidrs`.
- **One replica.** Concurrent requests share a creation within one process only. Two replicas that
  create one tenant's renderer at once are still kept apart by the record check every command
  makes (see [One command at a time](#one-command-at-a-time)): the replica that finds the other's
  record when its own renderer is ready discards its sandbox and answers as found, so the cost is
  a sandbox created for nothing, or, when both check before either writes, one that `prune`
  deletes. But each replica keeps its own rate limit and counts only its own creations in flight,
  so replicas together can create more than `MaxCreatesPerMinute`, and briefly exceed
  `MaxTenants`. Commands from the command line may run while the service does; the same check
  keeps them apart from it.
- **Probes.** Liveness on `/health/live`. Readiness on `/health/ready`, which lists the store's
  tenants, at most once every 30 seconds, as the quota does.
- **Shutdown.** The first `SIGTERM` stops taking requests and cancels the creations in flight; each
  deletes the sandbox it made, for at most two minutes, the requests waiting for it are answered
  `503`, and the service exits with `0`. Allow for that in the platform's termination grace period;
  a second signal stops the service at once, and `prune` deletes what it leaves behind.
- **Logs.** The service logs through ASP.NET Core's console logger, which the usual environment
  variables configure, such as `Logging__LogLevel__Default` and `Logging__Console__FormatterName=json`.
  ASP.NET Core's own entries for each request are left out (`Logging__LogLevel__Microsoft.AspNetCore`
  is `Warning` unless set). The provisioner's progress lines become log entries with the tenant as
  `TenantId`. Entries name tenants, sandboxes, and disk images; never a key, a renderer's credential,
  or a verifier, and nothing a refused request sent.

### Container image

[`Dockerfile`](Dockerfile) builds an image that runs `serve`: framework-dependent on the chiseled
ASP.NET Core runtime image, which has no shell or package manager, as the non-root user `app`
(UID 1654), on port 8080. Settings come from `Provisioner__*` environment variables.

```bash
docker build -f src/Atli.Reports.Provisioner/Dockerfile -t atli-reports-provisioner .
```

Run from the repository root. CI does not build the image. The other commands run from the same
image with its entrypoint replaced:
`docker run --entrypoint dotnet <image> atli-reports-provisioner.dll list`.

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

With tenants under `Provisioner:Service:TenantPrefixes`, step 2 can be
`rollout --disk-image <id> --stopped retire`. Then, of those tenants, only running renderers (and
disabled ones) are replaced; stopped ones are retired, and come back from the provisioning
service's disk image on their next conversion, so the rollout creates no renderer that would only
sit idle. Do step 4 for the provisioning service first: until it runs with the new
`DiskImageId`, retired tenants come back on the old image.

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
