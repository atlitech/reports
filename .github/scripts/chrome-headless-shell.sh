#!/usr/bin/env bash
# The chrome-headless-shell version the server image ships. The `ARG CHROME_VERSION=<version>` line in
# src/Atli.Reports.Server/Dockerfile is the single source of truth; the workflows read and update it
# through this script instead of parsing the Dockerfile themselves.
#
#   chrome-headless-shell.sh pinned [dockerfile]          print the pinned version
#   chrome-headless-shell.sh pin <version> [dockerfile]   pin <version>
#   chrome-headless-shell.sh compare <a> <b>              print older, same or newer: <a> against <b>
#   chrome-headless-shell.sh stable                       print Chrome for Testing's stable version
#   chrome-headless-shell.sh downloadable <version>       exit 0 when the linux64 and linux-arm64
#                                                         downloads exist, 1 when one is missing,
#                                                         3 when Chrome for Testing cannot tell
#
# Versions are Chrome's four numbers, such as 154.0.8037.92, compared number by number.
set -euo pipefail

default_dockerfile="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/src/Atli.Reports.Server/Dockerfile"
version_pattern='^[0-9]+(\.[0-9]+){3}$'
pin_pattern='^ARG CHROME_VERSION='
platforms=(linux64 linux-arm64)

fail() {
  echo "::error::$*" >&2
  exit 1
}

usage() {
  sed -n '6,12s/^# \{0,1\}//p' "${BASH_SOURCE[0]}" >&2
  exit 2
}

require_version() {
  [[ "$1" =~ $version_pattern ]] || fail "'$1' is not a Chrome version such as 154.0.8037.92."
}

pin_line() {
  local dockerfile="$1" lines
  [ -f "$dockerfile" ] || fail "$dockerfile does not exist."
  lines="$(grep -E "$pin_pattern" "$dockerfile" || true)"
  if [ -z "$lines" ] || [ "$(wc -l <<<"$lines")" -ne 1 ]; then
    fail "$dockerfile must have exactly one 'ARG CHROME_VERSION=<version>' line."
  fi
  echo "$lines"
}

pinned() {
  local dockerfile="${1:-$default_dockerfile}" version
  version="$(pin_line "$dockerfile")"
  version="${version#ARG CHROME_VERSION=}"
  [[ "$version" =~ $version_pattern ]] ||
    fail "$dockerfile pins CHROME_VERSION '$version', not a version such as 154.0.8037.92."
  echo "$version"
}

pin() {
  local version="$1" dockerfile="${2:-$default_dockerfile}" updated
  require_version "$version"
  pin_line "$dockerfile" >/dev/null
  updated="$(sed -E "s/${pin_pattern}.*/ARG CHROME_VERSION=${version}/" "$dockerfile")"
  # Written back in place, so the file keeps its mode; $(...) drops the final newline.
  printf '%s\n' "$updated" >"$dockerfile"
  [ "$(pinned "$dockerfile")" = "$version" ] || fail "Could not pin $version in $dockerfile."
}

compare() {
  local -a a b
  local i
  require_version "$1"
  require_version "$2"
  IFS=. read -r -a a <<<"$1"
  IFS=. read -r -a b <<<"$2"
  for i in 0 1 2 3; do
    if ((10#${a[i]} < 10#${b[i]})); then
      echo older
      return
    fi
    if ((10#${a[i]} > 10#${b[i]})); then
      echo newer
      return
    fi
  done
  echo same
}

stable() {
  local version
  version="$(
    curl -fsSL --retry 3 \
      https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions-with-downloads.json |
      jq -r '.channels.Stable.version // empty'
  )"
  [[ "$version" =~ $version_pattern ]] ||
    fail "Chrome for Testing did not return a stable version number (got '$version')."
  echo "$version"
}

# The URLs the Dockerfile's browser stage downloads.
downloadable() {
  local version="$1" platform url status missing=0
  require_version "$version"
  for platform in "${platforms[@]}"; do
    url="https://storage.googleapis.com/chrome-for-testing-public/${version}/${platform}/chrome-headless-shell-${platform}.zip"
    if ! status="$(curl -sS --retry 3 --head --output /dev/null --write-out '%{http_code}' "$url")"; then
      echo "::error::Could not reach $url." >&2
      exit 3
    fi
    case "$status" in
      200) echo "Available: $url" ;;
      404)
        echo "Missing: $url"
        missing=1
        ;;
      *)
        echo "::error::$url answered $status." >&2
        exit 3
        ;;
    esac
  done
  return "$missing"
}

[ "$#" -ge 1 ] || usage
command="$1"
shift
case "$command" in
  pinned) [ "$#" -le 1 ] || usage; pinned "$@" ;;
  pin) [ "$#" -ge 1 ] && [ "$#" -le 2 ] || usage; pin "$@" ;;
  compare) [ "$#" -eq 2 ] || usage; compare "$@" ;;
  stable) [ "$#" -eq 0 ] || usage; stable ;;
  downloadable) [ "$#" -eq 1 ] || usage; downloadable "$1" ;;
  *) usage ;;
esac
