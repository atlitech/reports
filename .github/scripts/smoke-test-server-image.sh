#!/usr/bin/env bash
# Smoke-tests an Atli Reports Server image. It starts a container with the seccomp profile
# deploy/seccomp/chromium.json, waits for /health/ready, converts a small HTML document and checks
# that the response is a PDF, checks that the OpenAPI document is served, checks that tini is PID 1
# and that no process in the container runs as root, checks that Chromium runs with its sandbox (no
# --no-sandbox, renderers outside the browser's user namespace), then stops the container and checks
# that the server shut down cleanly. Last, it starts the image under Docker's default seccomp profile
# and checks that it fails closed: not ready, with the remedy in its health details and its log. The
# containers' logs are printed when any check fails.
#
# Used by .github/workflows/server-image.yml (every image change) and server-image-publish.yml (each
# architecture's pushed image, before any tag points at it, for a release or a Chrome refresh). Run
# it locally against an image you built:
#
#   docker build -f src/Atli.Reports.Server/Dockerfile -t atli-reports-server .
#   .github/scripts/smoke-test-server-image.sh atli-reports-server
#
# Environment: READY_TIMEOUT_SECONDS (default 90) bounds the wait for /health/ready;
# SECCOMP_PROFILE (default deploy/seccomp/chromium.json in this checkout) is the profile to run with.
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <image>" >&2
  exit 2
fi

image="$1"
ready_timeout_seconds="${READY_TIMEOUT_SECONDS:-90}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
seccomp_profile="${SECCOMP_PROFILE:-$repo/deploy/seccomp/chromium.json}"
container="reports-server-smoke-$$-$RANDOM"
unprofiled_container="$container-default-seccomp"
holder_pid=""
work="$(mktemp -d)"
umask 077

# Exercise the shipped authentication path, not anonymous mode. Credentials are held in private
# files so curl's command line and failure diagnostics do not expose them.
key_id=smoke
credential="$key_id.$(openssl rand -hex 32)"
verifier="$(printf '%s' "$credential" | openssl dgst -sha256 -binary | openssl base64 -A)"
printf 'header = "X-Reports-Api-Key: %s"\n' "$credential" > "$work/client.curl"
cat > "$work/server.env" <<EOF
ReportsServer__Authentication__Mode=ApiKey
ReportsServer__Authentication__ApiKeys__0__Id=$key_id
ReportsServer__Authentication__ApiKeys__0__Hash=$verifier
ReportsServer__Authentication__ApiKeys__0__CallerId=smoke
ReportsServer__Authentication__ApiKeys__0__Permissions__0=reports.convert
ReportsServer__Authentication__ApiKeys__0__Permissions__1=reports.diagnostics
ATLI_SMOKE_SECRET=must-not-reach-the-browser
EOF
unset credential verifier

fail() {
  echo "::error::$*" >&2
  exit 1
}

cleanup() {
  local status=$?
  if [ -n "$holder_pid" ]; then
    kill "$holder_pid" 2>/dev/null || true
    wait "$holder_pid" 2>/dev/null || true
  fi
  for name in "$container" "$unprofiled_container"; do
    if [ "$status" -ne 0 ] && docker container inspect "$name" >/dev/null 2>&1; then
      echo "----- container logs ($name) -----"
      docker logs "$name" 2>&1 || true
      echo "----- container state -----"
      docker container inspect --format '{{json .State}}' "$name" || true
    fi
    docker rm --force "$name" >/dev/null 2>&1 || true
  done
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

[ -f "$seccomp_profile" ] || fail "The seccomp profile $seccomp_profile does not exist."
size_bytes="$(docker image inspect --format '{{.Size}}' "$image")"
echo "Image: $image ($((size_bytes / 1024 / 1024)) MiB)"
echo "Seccomp profile: $seccomp_profile"
# Chromium's sandbox needs unprivileged user namespaces; show what the host restricts.
for setting in kernel/apparmor_restrict_unprivileged_userns kernel/unprivileged_userns_clone \
  user/max_user_namespaces; do
  if [ -r "/proc/sys/$setting" ]; then
    echo "Host $(echo "$setting" | tr / .) = $(cat "/proc/sys/$setting")"
  fi
done

# The containers' shared settings: the authentication path, a read-only root with a bounded /tmp,
# no capabilities, and no privilege escalation, as in the documented deployments.
run_server() {
  local name="$1"
  shift
  # Publish on a free loopback port, so concurrent runs on one machine do not collide.
  docker run --detach --name "$name" --publish 127.0.0.1::8080 \
    --env-file "$work/server.env" --read-only --tmpfs /tmp:rw,nosuid,nodev,size=512m,mode=1777 \
    --cap-drop ALL --security-opt no-new-privileges:true "$@" "$image" >/dev/null
  echo "http://$(docker port "$name" 8080/tcp | head -n 1)"
}

base_url="$(run_server "$container" --security-opt "seccomp=$seccomp_profile")"
echo "Started $container at $base_url (AppArmor profile: $(docker container inspect --format '{{.AppArmorProfile}}' "$container"))"

echo "Waiting up to ${ready_timeout_seconds}s for /health/ready"
start=$SECONDS
until curl --silent --fail --max-time 5 --output "$work/ready.json" "$base_url/health/ready"; do
  if [ "$(docker container inspect --format '{{.State.Running}}' "$container")" != true ]; then
    fail "The container exited before /health/ready returned 200."
  fi
  if [ $((SECONDS - start)) -ge "$ready_timeout_seconds" ]; then
    fail "/health/ready did not return 200 within ${ready_timeout_seconds}s."
  fi
  sleep 1
done
echo "Ready after $((SECONDS - start))s: $(cat "$work/ready.json")"

status="$(curl --silent --show-error --max-time 10 --output "$work/unauthorized.json" \
  --write-out '%{http_code}' --header 'Content-Type: application/json' \
  --data '{"html":"<h1>Must not render</h1>"}' "$base_url/convert")"
[ "$status" = 401 ] || fail "Unauthenticated conversion returned $status, expected 401."
if grep -q 'description\|process\|/opt/' "$work/ready.json"; then
  fail "Anonymous readiness exposes internal diagnostics."
fi
echo "Unauthenticated conversions are rejected; readiness exposes no diagnostics"

# The body is Models/ConvertRequest.cs, serialized in camelCase.
status="$(
  curl --config "$work/client.curl" --silent --show-error --max-time 60 \
    --header 'Content-Type: application/json' \
    --data '{"html":"<!doctype html><html><body><h1>Atli Reports smoke test</h1><p>Hello, PDF.</p></body></html>","options":{"paperSize":"A4"}}' \
    --dump-header "$work/headers.txt" \
    --output "$work/output.pdf" \
    --write-out '%{http_code}' \
    "$base_url/convert"
)"
if [ "$status" != 200 ]; then
  fail "POST /convert returned $status: $(head -c 2000 "$work/output.pdf")"
fi
if ! grep -qi '^content-type: application/pdf' "$work/headers.txt"; then
  fail "POST /convert did not answer with Content-Type: application/pdf."
fi
if [ "$(head -c 4 "$work/output.pdf")" != "%PDF" ]; then
  fail "POST /convert returned a body that does not start with %PDF."
fi
echo "POST /convert: 200, $(wc -c <"$work/output.pdf" | tr -d ' ') bytes of PDF"

# The server builds its OpenAPI document at runtime, so only the NativeAOT binary itself shows that it
# can: a type the document needs but the trimmed binary lacks fails the request, not the build.
status="$(
  curl --config "$work/client.curl" --silent --show-error --max-time 10 \
    --output "$work/openapi.json" \
    --write-out '%{http_code}' \
    "$base_url/openapi/v1.json"
)"
if [ "$status" != 200 ]; then
  fail "GET /openapi/v1.json returned $status: $(head -c 2000 "$work/openapi.json")"
fi
openapi_field="$(grep -Eo '"openapi" *: *"[^"]+"' "$work/openapi.json" | head -n 1 || true)"
if [ -z "$openapi_field" ]; then
  fail "GET /openapi/v1.json returned a body without an openapi field: $(head -c 2000 "$work/openapi.json")"
fi
if ! grep -q '"/convert"' "$work/openapi.json"; then
  fail "GET /openapi/v1.json does not describe /convert."
fi
echo "GET /openapi/v1.json: 200, $openapi_field"

# One line per process: Name, Pid, PPid and the real, effective, saved and filesystem UIDs, read from
# /proc inside the container (whose PID namespace makes the entrypoint PID 1). The exec runs as the
# image's user, so it shows up as one more non-root process.
processes="$(
  docker exec "$container" sh -c \
    'for status in /proc/[0-9]*/status; do grep -E "^(Name|Pid|PPid|Uid):" "$status" 2>/dev/null | tr "\n" "\t"; echo; done'
)"
echo "Processes (pid, ppid, uid, name):"
pid1_name=""
server_found=false
root_found=false
while IFS=$'\t' read -r _ name _ pid _ ppid _ ruid euid suid fsuid _; do
  [ -n "$pid" ] || continue
  printf '  %6s %6s %6s  %s\n' "$pid" "$ppid" "$euid" "$name"
  if [ "$pid" = 1 ]; then
    pid1_name="$name"
  fi
  # comm is truncated to 15 characters.
  if [ "$ppid" = 1 ] && [ "$name" = "Atli.Reports.Se" ]; then
    server_found=true
  fi
  for uid in "$ruid" "$euid" "$suid" "$fsuid"; do
    if [ "$uid" = 0 ]; then
      root_found=true
    fi
  done
done <<<"$processes"

if [ "$pid1_name" != tini ]; then
  fail "PID 1 is '$pid1_name', not tini."
fi
if [ "$server_found" != true ]; then
  fail "No Atli.Reports.Server process is a child of tini."
fi
if [ "$root_found" = true ]; then
  fail "A process in the container runs as root."
fi
echo "tini is PID 1, the server is its child, and no process runs as root"

# Never print process environments: they contain credentials. Check only for our canary's name.
if docker exec "$container" sh -c '
  inspected=0
  for task in /proc/[0-9]*; do
    [ -r "$task/comm" ] || continue
    case "$(cat "$task/comm")" in
      chrome*|headless*)
        # Chromium can make sandboxed child processes non-dumpable. Require at least the
        # readable browser parent; do not mistake a denied /proc read for a passed check.
        browser_environment=$( (tr "\000" "\n" < "$task/environ") 2>/dev/null) || continue
        inspected=$((inspected + 1))
        if printf "%s\n" "$browser_environment" | grep -q "^ATLI_SMOKE_SECRET="; then exit 1; fi
        ;;
    esac
  done
  [ "$inspected" -gt 0 ]
'; then
  echo "Readable browser process environments contain no application secret canary"
else
  fail "Browser environment verification failed or no browser environment was readable."
fi

# Chromium's sandbox: a renderer exists while a conversion waits for a signal that never comes, so
# hold one open in the background while inspecting the processes.
curl --config "$work/client.curl" --silent --max-time 60 --output /dev/null \
  --header 'Content-Type: application/json' \
  --data '{"html":"<!doctype html><p>Holding a renderer</p>","options":{"waitForSignal":"smokeSandboxCheck","waitTimeoutSeconds":45}}' \
  "$base_url/convert" &
holder_pid=$!

# One line per browser process: pid, type (browser for the process the engine started), user
# namespace, and whether its command line carries --no-sandbox. Chromium rewrites its children's
# command lines into one space-separated string, so NULs become spaces before matching.
inspect_browser() {
  docker exec "$1" sh -c '
    for task in /proc/[0-9]*; do
      # A process that exits meanwhile (a closing renderer) is skipped.
      comm=$(cat "$task/comm" 2>/dev/null) || continue
      case "$comm" in chrome*|headless*) ;; *) continue ;; esac
      command_line=" $(tr "\000" " " < "$task/cmdline" 2>/dev/null) " || continue
      userns=$(readlink "$task/ns/user" 2>/dev/null) || continue
      [ "$command_line" != "  " ] || continue
      type=$(printf "%s" "$command_line" | grep -o -- " --type=[^ ]*" | head -n 1 | cut -d= -f2)
      no_sandbox=no
      case "$command_line" in *" --no-sandbox "*) no_sandbox=yes ;; esac
      echo "${task#/proc/} ${type:-browser} $userns $no_sandbox"
    done'
}

start=$SECONDS
until inspect_browser "$container" > "$work/browser.txt" && grep -q ' renderer ' "$work/browser.txt"; do
  if [ $((SECONDS - start)) -ge 20 ]; then
    cat "$work/browser.txt" >&2 || true
    fail "No renderer process appeared while a conversion was in flight."
  fi
  sleep 0.5
done
echo "Browser processes (pid, type, user namespace, --no-sandbox):"
sed 's/^/  /' "$work/browser.txt"
if grep -q ' yes$' "$work/browser.txt"; then
  fail "A browser process runs with --no-sandbox."
fi
browser_userns="$(awk '$2 == "browser" { print $3 }' "$work/browser.txt")"
if [ "$(printf '%s\n' "$browser_userns" | grep -c .)" != 1 ]; then
  fail "Expected one browser process with a readable user namespace, found: '$browser_userns'."
fi
while read -r pid type userns _; do
  if [ "$type" = renderer ] && [ "$userns" = "$browser_userns" ]; then
    fail "Renderer $pid shares the browser's user namespace ($userns)."
  fi
done < "$work/browser.txt"
kill "$holder_pid" 2>/dev/null || true
wait "$holder_pid" 2>/dev/null || true
holder_pid=""
echo "Chromium runs with its sandbox: no --no-sandbox, and renderers outside the browser's user namespace"

# tini forwards SIGTERM to the server, which drains and closes the browser; a clean shutdown exits 0.
docker stop --time 30 "$container" >/dev/null
exit_code="$(docker container inspect --format '{{.State.ExitCode}}' "$container")"
if [ "$exit_code" != 0 ]; then
  fail "The container exited with $exit_code after SIGTERM; a clean shutdown exits with 0."
fi
echo "Stopped cleanly (exit code 0)"

# Without the profile, Docker's default seccomp profile denies the user namespaces: the server must
# fail closed, never fall back to --no-sandbox, and say what to do in its health details and its log.
remedy='Chromium could not create its sandbox'
unprofiled_url="$(run_server "$unprofiled_container")"
echo "Started $unprofiled_container at $unprofiled_url with Docker's default seccomp profile"
start=$SECONDS
until curl --config "$work/client.curl" --silent --max-time 5 --output "$work/details.json" \
  "$unprofiled_url/health/details" && grep -q "$remedy" "$work/details.json"; do
  if [ "$(docker container inspect --format '{{.State.Running}}' "$unprofiled_container")" != true ]; then
    fail "The container without the seccomp profile exited instead of reporting the sandbox failure."
  fi
  if [ $((SECONDS - start)) -ge "$ready_timeout_seconds" ]; then
    fail "Without the seccomp profile, /health/details did not report the sandbox failure: $(cat "$work/details.json" 2>/dev/null)"
  fi
  sleep 1
done
status="$(curl --silent --max-time 5 --output /dev/null --write-out '%{http_code}' "$unprofiled_url/health/ready")"
[ "$status" = 503 ] || fail "Without the seccomp profile, /health/ready returned $status, expected 503."
docker logs "$unprofiled_container" > "$work/unprofiled.log" 2>&1
grep -q "$remedy" "$work/unprofiled.log" || fail "Without the seccomp profile, the log does not explain the sandbox failure."
if inspect_browser "$unprofiled_container" | grep -q ' yes$'; then
  fail "Without the seccomp profile, a browser runs with --no-sandbox."
fi
echo "Without the seccomp profile the server fails closed: /health/ready 503, the remedy in /health/details and the log"
docker rm --force "$unprofiled_container" >/dev/null

echo "Smoke test passed: $image"
