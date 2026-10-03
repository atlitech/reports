#!/usr/bin/env bash
# Validates the native Kubernetes example in its own disposable kind cluster. Requires Docker,
# kubectl, kind (or KIND_BIN), curl, Python 3, and OpenSSL. Never reads the active kubeconfig.
# The default kind CNI does NOT enforce NetworkPolicy: this validates API schema, probes,
# authentication, conversion, Chromium's sandbox under the Localhost seccomp profile, and rolling
# deployment, not cluster network isolation or HPA scaling.
#
# kind runs pods without an AppArmor profile, so on a host that restricts unprivileged user
# namespaces through AppArmor (Ubuntu 23.10+, kernel.apparmor_restrict_unprivileged_userns=1)
# Chromium cannot create its sandbox in them; set the sysctl to 0 for the test.
set -euo pipefail
umask 077

image="${1:-atli-reports-server:security}"
kind_bin="${KIND_BIN:-kind}"
node_image="kindest/node:v1.35.0@sha256:452d707d4862f52530247495d180205e029056831160e22870e37e3f6c1ac31f"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/atli-kubernetes-validation.XXXXXX")"
cluster="atli-reports-security-$(date +%s)-$$"
cluster_owned=false
forward_pid=""

fail() { echo "::error::$*" >&2; exit 1; }
kube() { kubectl --kubeconfig "$work/kubeconfig" --namespace reports-validation "$@"; }
stop_forward() {
  if [ -n "$forward_pid" ]; then
    kill "$forward_pid" 2>/dev/null || true
    wait "$forward_pid" 2>/dev/null || true
    forward_pid=""
  fi
}
cleanup() {
  local status=$?
  stop_forward
  if [ "$cluster_owned" = true ]; then
    if [ "$status" -ne 0 ] && [ -f "$work/kubeconfig" ]; then
      kube get pods -o wide || true
      kube get events --sort-by=.lastTimestamp || true
      kube logs --selector app=reports --tail=50 --all-containers=true || true
    fi
    "$kind_bin" delete cluster --name "$cluster" --kubeconfig "$work/kubeconfig" || status=1
  fi
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

for tool in docker kubectl curl python3 openssl "$kind_bin"; do
  command -v "$tool" >/dev/null || fail "Missing required tool: $tool"
done
docker image inspect "$image" >/dev/null
seccomp_profile="$repo/deploy/seccomp/chromium.json"
[ -f "$seccomp_profile" ] || fail "Missing $seccomp_profile."
if [ "$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns 2>/dev/null || echo 0)" = 1 ]; then
  echo "::warning::kernel.apparmor_restrict_unprivileged_userns=1: kind pods run without an AppArmor profile, so Chromium's sandbox cannot start in them. Set it to 0 for this test."
fi
if "$kind_bin" get clusters 2>/dev/null | grep -Fxq "$cluster"; then
  fail "Refusing to reuse an existing cluster."
fi
cluster_owned=true
"$kind_bin" create cluster --name "$cluster" --kubeconfig "$work/kubeconfig" \
  --image "$node_image" --wait 120s
"$kind_bin" load docker-image "$image" --name "$cluster"
# The manifest's Localhost seccomp profile, where the kubelet looks for it on every node.
for node in $("$kind_bin" get nodes --name "$cluster"); do
  docker exec "$node" mkdir -p /var/lib/kubelet/seccomp/atli-reports
  docker cp "$seccomp_profile" "$node:/var/lib/kubelet/seccomp/atli-reports/chromium.json"
done
kube create namespace reports-validation

# Validate the unmodified examples against the real API, including the optional HPA schema.
kube apply --dry-run=server -f "$repo/deploy/kubernetes/reports.yaml" \
  -f "$repo/deploy/kubernetes/hpa.yaml"
bash "$repo/scripts/create-reports-api-key.sh" "$work/credentials" kubernetes-validation
kube create secret generic reports-auth --from-env-file="$work/credentials/server.env"

# Only the image is adapted for this local test; the checked-in resource/security/probe settings
# are applied as written. Do not install the HPA without metrics-server.
kube apply --dry-run=client -f "$repo/deploy/kubernetes/reports.yaml" -o json > "$work/manifest.json"
python3 - "$work/manifest.json" "$image" <<'PY'
import json
import sys
path, image = sys.argv[1:]
with open(path) as source:
    manifest = json.load(source)
for resource in manifest['items']:
    if resource['kind'] == 'Deployment':
        resource['spec']['template']['spec']['containers'][0]['image'] = image
with open(path, 'w') as target:
    json.dump(manifest, target)
PY
kube apply -f "$work/manifest.json"
kube rollout status deployment/reports --timeout=180s

start_forward() {
  kube port-forward --address 127.0.0.1 service/reports :8080 > "$work/forward.log" 2>&1 &
  forward_pid=$!
  for _ in $(seq 1 30); do
    address="$(awk '/Forwarding from 127.0.0.1:/ { print $3; exit }' "$work/forward.log")"
    if [ -n "$address" ]; then
      base_url="http://$address"
      return
    fi
    kill -0 "$forward_pid" 2>/dev/null || fail "Port-forward exited unexpectedly."
    sleep 1
  done
  fail "Port-forward did not start."
}
start_forward

for probe in live ready; do
  curl --silent --show-error --fail --max-time 10 "$base_url/health/$probe" > "$work/$probe.json"
  if grep -Eq 'description|process|/opt/' "$work/$probe.json"; then
    fail "Anonymous probe exposed diagnostic details."
  fi
done
printf '%s' '{"html":"<!doctype html><h1>Kubernetes security validation</h1>"}' > "$work/request.json"
status="$(curl --silent --show-error --max-time 10 --output "$work/anonymous.json" \
  --write-out '%{http_code}' --header 'Content-Type: application/json' \
  --data-binary @"$work/request.json" "$base_url/convert")"
[ "$status" = 401 ] || fail "Anonymous conversion returned $status, expected 401."

convert() {
  local status
  status="$(curl --config "$work/credentials/client.curl" --silent --show-error --max-time 60 \
    --output "$work/report.pdf" --write-out '%{http_code}' --header 'Content-Type: application/json' \
    --data-binary @"$work/request.json" "$base_url/convert")"
  [ "$status" = 200 ] || fail "Authenticated conversion returned $status, expected 200."
  [ "$(head -c 4 "$work/report.pdf")" = %PDF ] || fail "Conversion did not produce a PDF."
}
convert

# Chromium's sandbox in every pod: no browser process runs with --no-sandbox, and the sandboxed
# zygote, which forks the renderers, is outside the browser's user namespace.
for pod in $(kube get pods --selector app=reports --output name); do
  # shellcheck disable=SC2016 # The pod's shell expands these.
  kube exec "$pod" -- sh -c '
    browser=""; separate=0
    for task in /proc/[0-9]*; do
      # A process that exits meanwhile (a closing renderer) is skipped.
      comm=$(cat "$task/comm" 2>/dev/null) || continue
      case "$comm" in chrome*|headless*) ;; *) continue ;; esac
      command_line=" $(tr "\000" " " < "$task/cmdline" 2>/dev/null) " || continue
      userns=$(readlink "$task/ns/user" 2>/dev/null) || continue
      case "$command_line" in *" --no-sandbox "*) echo "${task#/proc/} runs with --no-sandbox"; exit 1 ;; esac
      case "$command_line" in
        "  ") continue ;;
        *" --type="*) [ "$userns" = "$(readlink /proc/self/ns/user)" ] || separate=$((separate + 1)) ;;
        *) browser="$userns" ;;
      esac
    done
    [ -n "$browser" ] && [ "$browser" = "$(readlink /proc/self/ns/user)" ] && [ "$separate" -gt 0 ]
  ' || fail "Chromium does not run with its sandbox in $pod."
done
echo "Chromium runs with its sandbox in every pod"
status="$(curl --config "$work/credentials/client.curl" --silent --show-error --max-time 30 \
  --output "$work/blocked.json" --write-out '%{http_code}' --header 'Content-Type: application/json' \
  --data '{"html":"<img src=\"http://127.0.0.1:8080/health/live\">"}' "$base_url/convert")"
[ "$status" = 422 ] || fail "External asset request returned $status, expected policy rejection 422."
grep -q 'PolicyDenied' "$work/blocked.json" || fail "Asset rejection did not identify PolicyDenied."

stop_forward
kube rollout restart deployment/reports
kube rollout status deployment/reports --timeout=180s
start_forward
convert
echo "Kubernetes validation passed: schema, two ready replicas, probes, auth, PDF, Chromium's sandbox, network policy error, and rollout."
echo "NetworkPolicy enforcement and HPA scaling were not validated by this kind test."
