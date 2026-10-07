#!/usr/bin/env bash
# Called only after an updater has edited its allowlisted files on a clean checkout of main.
set -euo pipefail
: "${DEPENDENCY:?}" "${GH_TOKEN:?}" "${GITHUB_REPOSITORY:?}"
case "$DEPENDENCY" in
  tailwind) files=(src/Atli.Reports.Blazor.Tailwind.Build/BuildTailwindCss.cs src/Atli.Reports.Blazor.Tailwind/build/Atli.Reports.Blazor.Tailwind.props) ;;
  kind) files=(.github/dependency-pins.json) ;;
  seccomp) files=(.github/scripts/chromium-seccomp-profile.py deploy/seccomp/chromium.json) ;;
  gotenberg) files=(benchmarks/load/compose.yaml) ;;
  *) echo "Unknown dependency: $DEPENDENCY" >&2; exit 1 ;;
esac
if git diff --quiet -- "${files[@]}"; then exit 0; fi
branch="automation/$DEPENDENCY"
git add -- "${files[@]}"
# The diff fingerprint lets a maintainer decline this exact update without a daily replacement PR.
fingerprint="$(git diff --cached | git hash-object --stdin)"
marker="Dependency update: $DEPENDENCY/$fingerprint"
closed="$(gh pr list --repo "$GITHUB_REPOSITORY" --head "$branch" --state closed --json state,body --jq '.[] | select(.state == "CLOSED") | .body')"
if [[ "$closed" == *"$marker"* ]]; then
  echo "::notice::This exact update was declined; leaving it closed."
  exit 0
fi
git config user.name 'reports-dependency-updater[bot]'
git config user.email 'reports-dependency-updater[bot]@users.noreply.github.com'
git commit -m "build(deps): update $DEPENDENCY"
# gh supplies an ephemeral credential helper; no token is stored in the remote URL.
gh auth setup-git
git push --force-with-lease origin "HEAD:refs/heads/$branch"
body="$(mktemp)"
trap 'rm -f "$body"' EXIT
cat > "$body" <<BODY
Updates the pinned $DEPENDENCY dependency within its supported stable release line.

Versions and available upstream checksums are updated together. CI validates the changed dependency before merge. Seccomp updates require review of the generated security-policy diff; benchmark updates require checking the comparison baseline.

$marker
BODY
number="$(gh pr list --repo "$GITHUB_REPOSITORY" --head "$branch" --base main --state open --json number --jq '.[0].number // empty')"
if [ -n "$number" ]; then
  gh pr edit "$number" --repo "$GITHUB_REPOSITORY" --title "build(deps): update $DEPENDENCY" --body-file "$body"
else
  gh pr create --repo "$GITHUB_REPOSITORY" --base main --head "$branch" --title "build(deps): update $DEPENDENCY" --body-file "$body"
fi
