# Atli.Reports.Aspire.Hosting.Azure

Deploy the hosted Reports service through an Aspire AppHost: a gateway and internal provisioning
service in Azure Container Apps, with per-tenant renderers in Azure Container Apps Sandboxes.
Chromium's sandbox stays enabled in each renderer. This package requires .NET 10 and Aspire 13.6.
Azure Container Apps Sandboxes is a preview service and requires access in the selected region.

`AddAzureReportsEnvironment` provisions the application network with fixed NAT egress, a separate
renderer network with DNS denied, the sandbox group, a Key Vault record store, separate managed
identities, and the Container Apps environment. `WithReportsGateway` gives the gateway read-only
record access; `WithReportsProvisioner` gives the single provisioning service the scoped rights to
manage records and renderers. Renderers receive no workload identity.

`AddReportsRendererImage` builds a reusable renderer disk image during `aspire deploy`, before the
provisioning service is deployed. The source build uses the Azure Sandboxes `aca` CLI. Publishing
generates infrastructure artifacts without creating a disk image or Azure resources.

See the repository's [Azure deployment guide](https://github.com/atlitech/reports/blob/main/docs/azure-hosted-reports.md)
and [deployment AppHost](https://github.com/atlitech/reports/tree/main/examples/Atli.Reports.Azure.AppHost)
for credential setup, deployment commands, client wiring, and the limits of the previously tested
Azure architecture. Use the ordinary `AddReportsServer` integration for local development.
