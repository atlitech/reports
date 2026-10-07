# Aspire hosted reports: live Azure smoke test

On 2026-10-07, the Azure AppHost deployed successfully into a temporary resource group in
`eastus2`. Both tenants provisioned isolated renderers on demand. All 14 PDF conversions returned
HTTP 200, a complete PDF, and the expected request and tenant text (checked with `pdftotext`).
The gateway enforced authentication and tenant membership, and a stopped renderer resumed on
its next conversion. [Machine-readable results](2026-10-07-9702b35-aspire-azure-live.json).

The tested sources were `9702b35`, based on the merged Aspire implementation `ebea50a`. Tools:
Aspire CLI 13.6.0, Azure Sandboxes CLI 1.0.0-preview.4, Azure CLI credentials, and Docker. Gateway
and provisioner containers were built as Linux/amd64 images from the checkout; Aspire built the
renderer disk remotely from its staged Git-tracked source files. Fresh test credentials were
supplied through the four documented `Parameters__*` environment variables.

## Deployment finding

The first deployment failed Azure ARM preflight with `ValidationForResourceFailed` for the
sandbox group. Its Bicep resource omitted `properties`. Minimal ARM validation probes reproduced
the failure for both `2026-02-01-preview` and `2026-07-01`. An explicit `properties: {}` passed.
Adding that object to the embedded template allowed the same Aspire deployment to proceed.
A regression assertion now preserves it. No manually provisioned replacement resources were
used to make the deployment pass.

The corrected `aspire deploy --environment live-test-20261007 --non-interactive` completed.
Infrastructure provisioning took 38.3 seconds, renderer disk creation 166.5 seconds, and the
Container Apps environment 1,105.4 seconds (about 18 minutes). Azure reported `Waiting` for much
of environment creation, with no deployment error; its managed load balancer appeared before
completion. Gateway and provisioner deployment then took about 40 seconds each. This was a
fresh-environment provisioning delay, not a conversion latency measurement.

## Functional checks

Requests originated on the developer workstation over the public gateway HTTPS endpoint. The
fixture was a small inline HTML document with unique request and tenant labels; this is a smoke
test, not a throughput benchmark.

| Check | Result |
| --- | --- |
| Gateway liveness and readiness | HTTP 200 |
| Missing or invalid API key | HTTP 401 |
| Tenant outside the application's prefix | HTTP 403 |
| Missing tenant header | HTTP 400 |
| First conversion for tenant one | HTTP 200, correct PDF, 6.25 s |
| Warm conversion for tenant one | HTTP 200, correct PDF, 0.23 s |
| First conversion for tenant two | HTTP 200, correct PDF, 4.00 s |
| Ten conversions with two concurrent workers across both tenants | 10/10 correct PDFs, 0.21–0.23 s each |
| Conversion after observed automatic suspension | HTTP 200, correct PDF, 2.47 s |

Azure reported tenant two's renderer as `Stopped` before the last request and `Running`
afterwards, with the same sandbox ID. Both renderers had the 60-second memory auto-suspend
policy and `OnDemand` port activation. Suspension was observed through the data plane; it was
not forced with an operator command. The gateway and provisioner each had one running replica,
and only the gateway had external ingress.

## Isolation checks

All 12 infrastructure checks and seven renderer checks in the JSON results passed:

- The application and renderer VNets were separate with no peering. Application egress used
  NAT; renderers used the custom DNS address and an NSG denial for Azure platform DNS.
- The sandbox group had no workload identity. Key Vault used RBAC and purge protection.
  The gateway had Secrets User and no sandbox-group role. The provisioner had Secrets Officer
  and SandboxGroup Data Owner.
- Two distinct sandbox IDs were labelled for the two tenants. Both used the `renderers` VNet
  connection, deny-all egress, and port ingress restricted to the application NAT's `/32`.
- A direct renderer request from the workstation returned HTTP 403. The provisioner's internal
  HTTPS address returned HTTP 404 from the public internet.

These checks inspect the deployed configuration and exercise access boundaries. They do not
repeat the earlier hostile-workload, resource-exhaustion, rollout, or long-duration load tests,
and do not close every hosted-service production acceptance gate.

## Validation and cleanup

All 13 Azure integration tests and formatting checks passed locally. The PR's first CI run hit
an unrelated Blazor browser-start failure (`BrowserUnavailable` instead of `SignalTimeout`);
the failed checks passed on rerun without a source change.

`aspire destroy` accepted deletion of the temporary resource group and returned success with
Azure deletion still in progress. Teardown took about 30 minutes. The empty environment remained
`ScheduledForDelete` after its managed infrastructure group disappeared, with no resource locks
or failed deletion operations reported. During troubleshooting, its deletion was retried through
Azure CLI. The detached test VNets, NAT gateway, public IP, and NSG were then removed directly.
After the environment disappeared, deletion of the empty resource group was retried.

Both the temporary resource group and the Container Apps managed infrastructure group were
verified absent with `az group exists`. The final verification timestamp is recorded in the JSON
results. The vault remains soft-deleted under its 90-day purge protection, with scheduled purge
on 2027-01-05; no active vault remains.
