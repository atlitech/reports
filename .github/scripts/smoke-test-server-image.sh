#!/usr/bin/env bash
# Smoke-tests an Atli Reports Server image. It starts a container, waits for /health/ready, converts a
# small HTML document and checks that the response is a PDF, checks that the OpenAPI document is
# served, checks that tini is PID 1 and that no process in the container runs as root, then stops the
# container and checks that the server shut down cleanly. The container's logs are printed when any
# check fails.
#
# Used by .github/workflows/server-image.yml (every image change) and release.yml (the pushed release
# image). Run it locally against an image you built:
#
#   docker build -f src/Atli.Reports.Server/Dockerfile -t atli-reports-server .
#   .github/scripts/smoke-test-server-image.sh atli-reports-server
#
# Environment: READY_TIMEOUT_SECONDS (default 90) bounds the wait for /health/ready.
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <image>" >&2
  exit 2
fi

image="$1"
ready_timeout_seconds="${READY_TIMEOUT_SECONDS:-90}"
container="reports-server-smoke-$$-$RANDOM"
work="$(mktemp -d)"

fail() {
  echo "::error::$*" >&2
  exit 1
}

cleanup() {
  local status=$?
  if [ "$status" -ne 0 ] && docker container inspect "$container" >/dev/null 2>&1; then
    echo "----- container logs ($container) -----"
    docker logs "$container" 2>&1 || true
    echo "----- container state -----"
    docker container inspect --format '{{json .State}}' "$container" || true
  fi
  docker rm --force "$container" >/dev/null 2>&1 || true
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

size_bytes="$(docker image inspect --format '{{.Size}}' "$image")"
echo "Image: $image ($((size_bytes / 1024 / 1024)) MiB)"

# Publish on a free loopback port, so concurrent runs on one machine do not collide.
docker run --detach --name "$container" --publish 127.0.0.1::8080 "$image" >/dev/null
address="$(docker port "$container" 8080/tcp | head -n 1)"
base_url="http://$address"
echo "Started $container at $base_url"

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

# The body is Models/ConvertRequest.cs, serialized in camelCase.
status="$(
  curl --silent --show-error --max-time 60 \
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
  curl --silent --show-error --max-time 10 \
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

# tini forwards SIGTERM to the server, which drains and closes the browser; a clean shutdown exits 0.
docker stop --time 30 "$container" >/dev/null
exit_code="$(docker container inspect --format '{{.State.ExitCode}}' "$container")"
if [ "$exit_code" != 0 ]; then
  fail "The container exited with $exit_code after SIGTERM; a clean shutdown exits with 0."
fi
echo "Stopped cleanly (exit code 0)"
echo "Smoke test passed: $image"
