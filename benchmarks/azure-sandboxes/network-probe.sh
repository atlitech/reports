#!/bin/bash
# What a compromised renderer could reach from inside its sandbox, as key=value lines. Runs in the
# server image (bash, no curl): plain HTTP and TCP over /dev/tcp. Never prints a token.
tcp() { timeout 5 bash -c "exec 3<>/dev/tcp/$1/$2" 2>/dev/null && echo open || echo blocked; }
http_status() {
  timeout 10 bash -c "exec 3<>/dev/tcp/$1/80; printf 'GET / HTTP/1.1\r\nHost: $1\r\nConnection: close\r\n\r\n' >&3; head -n 1 <&3" 2>/dev/null |
    awk '{ print $2 }' | tr -d '\r'
}
echo "http_example_com=$(http_status example.com || true)"
# By address, so the answer does not depend on name resolution.
echo "http_1_1_1_1=$(http_status 1.1.1.1 || true)"
echo "tcp_1_1_1_1_443=$(tcp 1.1.1.1 443)"
echo "tcp_8_8_8_8_53=$(tcp 8.8.8.8 53)"
echo "tcp_imds_169_254_169_254_80=$(tcp 169.254.169.254 80)"
if timeout 20 getent hosts example.com >/dev/null; then echo "dns_resolves_example_com=yes"; else echo "dns_resolves_example_com=no"; fi
# The platform's managed-identity endpoint. Without an identity on the sandbox group it must refuse.
if [ -n "${IDENTITY_ENDPOINT:-}" ]; then
  hostport=$(echo "$IDENTITY_ENDPOINT" | sed -E 's#^https?://([^/]+).*#\1#')
  path=$(echo "$IDENTITY_ENDPOINT" | sed -E 's#^https?://[^/]+##')
  host=${hostport%:*}; port=${hostport##*:}; [ "$port" = "$hostport" ] && port=80
  response=$(timeout 10 bash -c "exec 3<>/dev/tcp/$host/$port; printf 'GET $path?api-version=2019-08-01&resource=https://management.azure.com/ HTTP/1.1\r\nHost: $hostport\r\nX-IDENTITY-HEADER: %s\r\nConnection: close\r\n\r\n' \"\$IDENTITY_HEADER\" >&3; cat <&3" 2>/dev/null)
  status=$(echo "$response" | head -n 1 | awk '{ print $2 }' | tr -d '\r')
  if echo "$response" | grep -q '"access_token"'; then issued=yes; else issued=no; fi
  error=$(echo "$response" | grep -o '"error":"[^"]*"' | head -n 1 | cut -d'"' -f4)
  echo "identity_endpoint_status=$status identity_token_issued=$issued identity_error=$error"
else
  echo "identity_endpoint_status=absent"
fi
