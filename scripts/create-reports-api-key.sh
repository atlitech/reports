#!/usr/bin/env bash
# Writes a new local credential and the server's SHA-256 verifier without printing the credential.
# Run from the repository root. Existing files are never overwritten.
set -euo pipefail
umask 077

output_dir="${1:-.reports-secrets}"
caller_id="${2:-local-app}"
if [[ ! "$caller_id" =~ ^[a-zA-Z0-9_-]+$ ]]; then
  echo "Caller ID must contain only letters, digits, underscores, and hyphens." >&2
  exit 2
fi
command -v openssl >/dev/null || { echo "OpenSSL is required." >&2; exit 1; }
mkdir -p "$output_dir"
for name in server.env client.env client.curl; do
  if [ -e "$output_dir/$name" ]; then
    echo "Refusing to overwrite $output_dir/$name; use a new directory for rotation." >&2
    exit 1
  fi
done

key_id="reports-$(openssl rand -hex 6)"
credential="$key_id.$(openssl rand -hex 32)"
verifier="$(printf '%s' "$credential" | openssl dgst -sha256 -binary | openssl base64 -A)"

# noclobber also protects against concurrent generation after the existence check above.
set -C
cat > "$output_dir/server.env" <<EOF
ReportsServer__Authentication__Mode=ApiKey
ReportsServer__Authentication__ApiKeys__0__Id=$key_id
ReportsServer__Authentication__ApiKeys__0__Hash=$verifier
ReportsServer__Authentication__ApiKeys__0__CallerId=$caller_id
ReportsServer__Authentication__ApiKeys__0__Permissions__0=reports.convert
ReportsServer__Authentication__ApiKeys__0__Permissions__1=reports.diagnostics
EOF
printf 'ReportsClient__ApiKey=%s\n' "$credential" > "$output_dir/client.env"
printf 'header = "X-Reports-Api-Key: %s"\n' "$credential" > "$output_dir/client.curl"
unset credential verifier
printf 'Created %s/server.env, client.env, and client.curl (owner access only).\n' "$output_dir"
echo "Keep client files private; provision server.env to the report server."
