# Hosted reports on Azure with Aspire

The deployment AppHost in `examples/Atli.Reports.Azure.AppHost` brings the tested hosted renderer
architecture into Aspire. A gateway receives authenticated conversion requests, and an internal
provisioning service creates an isolated renderer for a tenant's first request. Renderers run in
Azure Container Apps Sandboxes with Chromium's sandbox enabled. They suspend when idle and wake
through the platform's OnDemand port proxy.

This is distinct from `deploy/azure/reports.bicep`, which deploys one integrated rendering
container. The hosted AppHost preserves the separate gateway, provisioner, and renderer roles
from the [Azure workspace tests](../benchmarks/results/2026-10-04-f03e90e-hosted-renderers-workspaces-amd64.md).
Those tests validate the architecture; they do not constitute a live test of this new Aspire
deployment path or close the [hosted-service production acceptance gates](hosted-renderers.md#production-acceptance-gates).

## Resources and access

- The gateway and provisioner run in a workload-profile Container Apps environment on an
  application VNet with a fixed NAT egress address. The example exposes the authenticated gateway
  publicly; the provisioner has internal HTTPS ingress only.
- A separate renderer VNet has an unused DNS server address and an NSG rule denying Azure
  platform DNS. The sandbox group has no workload managed identity. The provisioner applies
  deny-all egress, the renderer network connection, and a source allowlist containing only the
  application's NAT address to each new renderer.
- Key Vault stores renderer records and credentials. The gateway identity has Secrets User;
  the provisioner identity has Secrets Officer and SandboxGroup Data Owner. Aspire supplies a
  separate registry pull identity to both services. The gateway has no sandbox-group
  role. The deploying identity receives scoped SandboxGroup Data Owner to build disk images.
- Both services start with one replica. Keep the provisioner at one replica: its tenant locks,
  creation budgets, and retirement coordination are in-process. Gateway limits are also per
  replica; increasing gateway replicas requires reviewing aggregate admission limits.

`AddAzureReportsEnvironment` owns this infrastructure through an embedded Bicep module and the
native Aspire Container Apps environment. `AddReportsGateway` and `AddReportsProvisioner` remain
ordinary container resources, with authentication, health probes, and tenant configuration.

## Prerequisites

Use .NET 10, Aspire CLI 13.6, Git, Azure CLI, Docker or Podman, and the Azure Sandboxes `aca` CLI.
Authenticate Azure CLI in the intended tenant. The selected subscription and region must have
access to the Azure Container Apps Sandboxes preview. The deployment identity needs permission
to create the listed resources and scoped role assignments.

The example builds the gateway and provisioner from this checkout as Linux/amd64 images and
pushes them to Aspire's container registry. It builds the renderer disk remotely from the server
Dockerfile with `aca`. Only Git-tracked build inputs are staged, including their current working
tree contents; untracked files are excluded. Commit new renderer source files before deployment.
The deployment therefore works before the new Atli NuGet packages and
container images are released.

## Publish and deploy

From the repository root, inspect the generated infrastructure without deploying anything:

```bash
aspire publish --apphost examples/Atli.Reports.Azure.AppHost/Atli.Reports.Azure.AppHost.csproj \
  -o artifacts/hosted-azure --non-interactive
```

Publishing requires neither credentials nor a live sandbox group. The disk image is a
deployment-time value; applying the published Bicep directly is not the complete deployment.
Use `aspire deploy` so the image preparation step runs before the provisioner's deployment.

Generate two independent credentials and their SHA-256 verifiers without printing them:

```bash
python3 scripts/create-hosted-reports-secrets.py
set -a
source .reports-secrets/hosted.env
set +a
export Azure__SubscriptionId='<subscription-id>'
export Azure__Location='eastus2'
export Azure__ResourceGroup='<resource-group>'

aspire deploy --apphost examples/Atli.Reports.Azure.AppHost/Atli.Reports.Azure.AppHost.csproj \
  --non-interactive
```

The private file supplies `Parameters__reportsKey`, `Parameters__reportsKeyHash`,
`Parameters__provisionerKey`, and `Parameters__provisionerKeyHash`. The example's key IDs are
`application` and `gateway`. The report credential belongs to the application calling the
gateway. The provisioner credential belongs only to the gateway. Services receive the verifier
for incoming authentication; the gateway also receives its outgoing provisioner credential.
In CI, provide these same parameter names from the pipeline's secret store.

The helper refuses to overwrite an existing credential file. Rotation requires coordinating
the service verifiers and client credentials; generating new values alone does not perform a
zero-downtime rotation.

## Call the service

The example owns tenant IDs beginning with `app-`, with at most 100 tenants and 20 creations
per minute. A tenant ID such as `app-workspace1` selects one isolated renderer. Do not let an
untrusted end user choose another workspace's tenant ID; derive it from your application's
authenticated workspace membership.

Call `POST /convert` at the gateway's deployed URL with `X-Reports-Api-Key` containing the report
credential and `X-Reports-Tenant: app-workspace1`. The first conversion creates the renderer;
later conversions reuse it. Requests over admission or creation budgets can return `503` with
`Retry-After`. Inline assets: renderer document networking is disabled.

For a .NET application, reference the gateway from its AppHost resource with
`.WithReference(gateway)`, then configure its client:

```csharp
builder.AddReportsClient("reports-gateway")
  .ConfigureHttpClient(client =>
    client.DefaultRequestHeaders.Add("X-Reports-Tenant", "app-workspace1"));
```

This example binds a client to one workspace. A multi-workspace application needs a request-scoped
handler that derives the header from authenticated membership, rather than mutating shared
default headers for each request. The example credential grants conversion only; enable tenant
management explicitly with `allowTenantManagement: true` when the application should delete
its tenants through the gateway API.

## Operations

The gateway supports OTLP telemetry; configure its deployed `OTEL_EXPORTER_OTLP_ENDPOINT` and
any required authentication for your collector. The local Aspire exporter does not automatically
configure an Azure telemetry destination. Both services expose `/health/live` and
`/health/ready`; the provisioner does not currently export OTLP. The deployment gives shutdown
150 seconds for in-flight work to drain and uses a single active revision.

Deploying a new renderer image updates the provisioner's default for newly created renderers.
Existing renderer records continue to reference their current disk. Use the provisioner's
documented [rollout procedure](../src/Atli.Reports.Provisioner/README.md#rollout) to replace them;
serialize operator commands with the provisioning service as described in that guide. Never
delete a disk while an active or suspended renderer still uses it.

For local development, use the original `examples/Atli.Reports.AppHost`; the Azure AppHost accepts
publish and deploy operations only. Deleting the Azure environment destroys renderer and record
state. The record vault enables purge protection and retains deleted secrets for 90 days.
