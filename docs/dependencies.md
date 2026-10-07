# Dependency maintenance

Dependabot proposes weekly GitHub Actions, NuGet (including CSharpier), .NET SDK, and devcontainer
updates. The production server/provisioner base-image digests are checked daily. Minor/patch
updates to related Microsoft, Azure, OpenTelemetry, and example Aspire packages are grouped;
other major upgrades remain separate PRs. Reviewers approve updates after `Dependency validation`
passes; updates are not automatically approved or merged.

The root NuGet job follows the solution and central package file. The example AppHost SDK,
`Aspire.Hosting.AppHost`, and `Aspire.Hosting.Testing` must advance together. The integration's
`Aspire.Hosting` 13.0 compatibility floor only receives patches; its preview Kubernetes testing
package stays pinned pending compatibility review. MessagePack stays on patched 2.x for that older
Aspire dependency graph. Microsoft framework packages and the .NET SDK stay within their current
major version until a coordinated framework migration. These exceptions do not remove security
alerts. A daily NuGet audit fails on known direct or transitive vulnerabilities, including those
in deliberately pinned packages; maintainers must resolve these explicitly.

Custom downloads are checked by `Custom dependency updates` weekly (or manually):

- Tailwind: stable v4 only; downloads and verifies every supported platform against the upstream
  checksum list, then changes both defaults and all pinned checksums together.
- kind: stable releases in its current major; verifies the Linux amd64 binary against its checksum,
  then updates `.github/dependency-pins.json`. The server's Kubernetes smoke test uses that pin.
- Seccomp: follows the `moby/profiles` seccomp tags, pins the resolved commit and content digest,
  regenerates the policy using our existing invariants, and opens a PR for security-policy review.
- Gotenberg: stable releases within the benchmark's major and digest-only rebuilds; opens its own
  PR so a reviewer can assess changes to the benchmark comparison baseline.

Chrome has its existing daily updater. Custom and Chrome PRs use the automation GitHub App
(`AUTOMATION_APP_CLIENT_ID` variable, `AUTOMATION_APP_PRIVATE_KEY` secret, Contents and Pull requests
write permissions) so PR checks run. Missing credentials fail the workflow. No workflow-writing
permission is needed: kind's pins live in JSON. Custom updater dry runs read upstream releases and
validate available updates without editing files or opening PRs. Closing a custom PR rejects that
exact diff; a newer update can produce another PR. Actions failures and Dependabot alerts should
be included in the maintainers' GitHub notification subscriptions.

`Dependency validation` waits for the ordinary build/tests/pack and all three Tailwind consumer
platforms. Runtime/dependency/workflow changes also require Aspire end-to-end tests and native
amd64/arm64 server and provisioner smoke tests. Devcontainer changes build the container;
benchmark-image changes validate Compose and pull the pinned image. Docs-only changes skip the
expensive runtime jobs while still completing the required gate.

Merging approved browser or base-image digest changes triggers `Refresh released images`.
It rebuilds released application source with those pins, rejecting changes to base-image names
or tags. It tests each architecture before moving any tag. Every built artifact gets a distinct
immutable `-image<hash>` tag; existing Chrome-only immutable tags are left untouched. Mutable
version/major-minor/latest tags move only when they still point at the image selected for refresh.
The provisioner enters this lifecycle with the first release containing its publishing workflow.
Dependency PRs and refreshes do not deploy running containers: consumers pull the refreshed image
or roll their provisioned renderer disk images forward through their normal rollout procedure.

Local checks:

```sh
python3 -m unittest discover -s .github/tests -v
actionlint
shellcheck .github/scripts/server-image.sh .github/scripts/open-dependency-pr.sh
python3 .github/scripts/dependency-updates.py tailwind  # read-only; also kind/seccomp/gotenberg
```
