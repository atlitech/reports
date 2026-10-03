#!/usr/bin/env python3
"""Exercise JWT discovery and RSA validation in the shipped NativeAOT container.

Usage: python3 .github/scripts/smoke-test-server-jwt.py <image>
Requires Python 3.10+, OpenSSL, and Docker; no Python packages are needed. A temporary HTTPS
issuer serves only discovery metadata and public signing keys on an ephemeral host port. The
container trusts its temporary CA through SSL_CERT_FILE. Production HTTPS/issuer/signature
validation remains enabled. Signing keys stay in a private temporary directory; tokens stay
in memory, never in command arguments or logs. All fixture resources are removed on exit.
The container runs with the seccomp profile deploy/seccomp/chromium.json (or SECCOMP_PROFILE), which
Chromium's sandbox needs.
"""

import argparse
import base64
import hashlib
import http.server
import json
import os
import pathlib
import re
import secrets
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request

SECCOMP_PROFILE = pathlib.Path(
    os.environ.get("SECCOMP_PROFILE")
    or pathlib.Path(__file__).resolve().parents[2] / "deploy" / "seccomp" / "chromium.json"
)


def run(*arguments, data=None, timeout=30):
    result = subprocess.run(
        arguments, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        timeout=timeout, check=False,
    )
    if result.returncode:
        # Command arguments may contain private file paths, but never credentials. Do not dump
        # arbitrary subprocess output, especially signing operations' binary output.
        raise RuntimeError(f"{arguments[0]} {arguments[1]} failed with exit code {result.returncode}")
    return result.stdout


def encode(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode("ascii")


def encode_json(value):
    return encode(json.dumps(value, separators=(",", ":")).encode("utf-8"))


def create_keys(work):
    ca_key, ca_cert = work / "ca.key", work / "ca.pem"
    tls_key, tls_csr, tls_cert = work / "tls.key", work / "tls.csr", work / "tls.pem"
    jwt_key = work / "jwt.key"
    run("openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
        "-subj", "/CN=Atli Reports JWT test CA", "-keyout", str(ca_key), "-out", str(ca_cert),
        "-addext", "basicConstraints=critical,CA:TRUE",
        "-addext", "keyUsage=critical,keyCertSign,cRLSign")
    run("openssl", "req", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=host.docker.internal",
        "-keyout", str(tls_key), "-out", str(tls_csr))
    extensions = work / "tls-extensions.cnf"
    extensions.write_text(
        "subjectAltName=DNS:host.docker.internal,DNS:localhost,IP:127.0.0.1\n"
        "basicConstraints=critical,CA:FALSE\n"
        "keyUsage=critical,digitalSignature,keyEncipherment\n"
        "extendedKeyUsage=serverAuth\n", encoding="ascii",
    )
    run("openssl", "x509", "-req", "-in", str(tls_csr), "-CA", str(ca_cert),
        "-CAkey", str(ca_key), "-CAcreateserial", "-days", "1", "-out", str(tls_cert),
        "-extfile", str(extensions))
    run("openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048",
        "-pkeyopt", "rsa_keygen_pubexp:65537", "-out", str(jwt_key))
    modulus = run("openssl", "rsa", "-in", str(jwt_key), "-noout", "-modulus").strip()
    if not modulus.startswith(b"Modulus="):
        raise RuntimeError("OpenSSL did not provide the public RSA modulus")
    public_modulus = bytes.fromhex(modulus.removeprefix(b"Modulus=").decode("ascii"))
    key_id = hashlib.sha256(public_modulus).hexdigest()[:24]
    public_key = {"kty": "RSA", "use": "sig", "alg": "RS256", "kid": key_id,
                  "n": encode(public_modulus), "e": "AQAB"}
    # Only the public trust anchor is mounted into the non-root container.
    ca_cert.chmod(0o644)
    return ca_cert, tls_cert, tls_key, jwt_key, public_key


class Issuer(http.server.ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, tls_cert, tls_key, public_key):
        super().__init__(("0.0.0.0", 0), IssuerHandler)
        self.authority = f"https://host.docker.internal:{self.server_port}"
        self.documents = {
            "/.well-known/openid-configuration": {
                "issuer": self.authority,
                "jwks_uri": self.authority + "/keys",
                "response_types_supported": ["token"],
                "subject_types_supported": ["public"],
                "id_token_signing_alg_values_supported": ["RS256"],
            },
            "/keys": {"keys": [public_key]},
        }
        self.requested_paths = set()
        self.requests_lock = threading.Lock()
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.load_cert_chain(str(tls_cert), str(tls_key))
        self.socket = context.wrap_socket(self.socket, server_side=True)


class IssuerHandler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        document = self.server.documents.get(self.path)
        if document is None:
            self.send_error(404)
            return
        with self.server.requests_lock:
            self.server.requested_paths.add(self.path)
        body = json.dumps(document).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_arguments):
        pass


def mint_token(jwt_key, key_id, authority, overrides=None):
    now = int(time.time())
    claims = {"iss": authority, "aud": "reports-api", "sub": "smoke-application",
              "iat": now - 5, "nbf": now - 5, "exp": now + 600,
              "roles": ["reports.convert"]}
    claims.update(overrides or {})
    unsigned = encode_json({"alg": "RS256", "typ": "at+jwt", "kid": key_id}) + "." + encode_json(claims)
    signature = run("openssl", "dgst", "-sha256", "-sign", str(jwt_key), data=unsigned.encode("ascii"))
    return unsigned + "." + encode(signature)


def request(url, token=None, convert=False):
    headers = {}
    body = None
    if convert:
        headers["Content-Type"] = "application/json"
        body = json.dumps({"html": "<!doctype html><h1>Authenticated NativeAOT conversion</h1>"}).encode()
    if token is not None:
        headers["Authorization"] = "Bearer " + token
    outgoing = urllib.request.Request(url, data=body, headers=headers)
    # Never follow a redirect with a document or credential, including inside the smoke harness.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *_arguments, **_keywords):
            return None
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        response = opener.open(outgoing, timeout=90 if convert else 5)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.headers.get("Content-Type", ""), response.read()


def check_conversion(base_url, label, expected, token=None):
    status, media_type, body = request(base_url + "/convert", token, convert=True)
    if status != expected:
        raise RuntimeError(f"{label}: expected HTTP {expected}, received HTTP {status}")
    if expected == 200:
        if not media_type.startswith("application/pdf") or not body.startswith(b"%PDF-"):
            raise RuntimeError(f"{label}: success response was not a PDF")
    else:
        expected_kind = "Unauthorized" if expected == 401 else "Forbidden"
        if json.loads(body).get("kind") != expected_kind:
            raise RuntimeError(f"{label}: incorrect problem kind")
    print(f"{label}: HTTP {status}", flush=True)


def sanitized_logs(container, tokens):
    try:
        logs = run("docker", "logs", container).decode("utf-8", errors="replace")
        for token in tokens:
            logs = logs.replace(token, "[redacted JWT]")
        logs = re.sub(r"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*", "[redacted JWT]", logs)
        print(logs[-12000:], file=sys.stderr)
    except (RuntimeError, subprocess.TimeoutExpired):
        pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    arguments = parser.parse_args()
    os.umask(0o077)
    container = "reports-jwt-smoke-" + secrets.token_hex(6)
    tokens = []
    started_container = False
    requested_container = False
    issuer = None
    thread = None
    with tempfile.TemporaryDirectory(prefix="reports-jwt-smoke-") as directory:
        work = pathlib.Path(directory)
        try:
            ca_cert, tls_cert, tls_key, jwt_key, public_key = create_keys(work)
            issuer = Issuer(tls_cert, tls_key, public_key)
            thread = threading.Thread(target=issuer.serve_forever, daemon=True)
            thread.start()
            settings = work / "server.env"
            settings.write_text(
                "ReportsServer__Authentication__Mode=JwtBearer\n"
                f"ReportsServer__Authentication__Jwt__Authority={issuer.authority}\n"
                "ReportsServer__Authentication__Jwt__Audience=reports-api\n"
                "ReportsEngine__Browser__WarmUpOnStartup=true\n"
                "SSL_CERT_FILE=/test-ca.pem\n", encoding="ascii",
            )
            docker_arguments = ["docker", "run", "--detach", "--name", container,
                                "--publish", "127.0.0.1::8080", "--env-file", str(settings),
                                "--mount", f"type=bind,source={ca_cert},target=/test-ca.pem,readonly",
                                "--read-only", "--tmpfs", "/tmp:rw,nosuid,nodev,size=512m,mode=1777",
                                "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
                                "--security-opt", f"seccomp={SECCOMP_PROFILE}"]
            if sys.platform.startswith("linux"):
                docker_arguments += ["--add-host", "host.docker.internal:host-gateway"]
            requested_container = True
            run(*docker_arguments, arguments.image)
            started_container = True
            address = run("docker", "port", container, "8080/tcp").decode().splitlines()[0]
            base_url = "http://" + address
            deadline = time.monotonic() + int(os.environ.get("READY_TIMEOUT_SECONDS", "90"))
            while True:
                try:
                    status, _, body = request(base_url + "/health/ready")
                    if status == 200:
                        if set(json.loads(body)) != {"status"}:
                            raise RuntimeError("Anonymous readiness returned detailed diagnostics")
                        break
                except (urllib.error.URLError, TimeoutError, ConnectionError):
                    pass
                if time.monotonic() >= deadline:
                    raise RuntimeError("The JWT container did not become ready")
                if run("docker", "inspect", "--format", "{{.State.Running}}", container).strip() != b"true":
                    raise RuntimeError("The JWT container exited during startup")
                time.sleep(0.25)
            print("JWT container ready; anonymous health is minimal", flush=True)
            check_conversion(base_url, "Missing credential", 401)
            now = int(time.time())
            cases = [
                ("Valid RSA access token", 200, {}),
                ("Expired token", 401, {"iat": now - 900, "nbf": now - 900, "exp": now - 300}),
                ("Wrong audience", 401, {"aud": "another-api"}),
                ("Audience trailing slash", 401, {"aud": "reports-api/"}),
                ("Wrong issuer", 401, {"iss": "https://another-issuer.example.test"}),
                ("Future validity", 401, {"nbf": now + 300}),
                ("Missing permission", 403, {"roles": []}),
                ("Compound role", 403, {"roles": ["other reports.convert"]}),
                ("Mixed-case internal permission", 403, {"roles": [], "ATLI.REPORTS.PERMISSION": "reports.convert"}),
            ]
            for label, expected, overrides in cases:
                token = mint_token(jwt_key, public_key["kid"], issuer.authority, overrides)
                tokens.append(token)
                check_conversion(base_url, label, expected, token)
            token = mint_token(jwt_key, public_key["kid"], issuer.authority)
            signing_input, signature = token.rsplit(".", 1)
            signature_bytes = bytearray(base64.urlsafe_b64decode(signature + "=" * (-len(signature) % 4)))
            signature_bytes[0] ^= 1
            tampered = signing_input + "." + encode(signature_bytes)
            tokens.append(tampered)
            check_conversion(base_url, "Invalid RSA signature", 401, tampered)
            unsigned = encode_json({"alg": "none", "typ": "at+jwt"}) + "." + token.split(".")[1] + "."
            tokens.append(unsigned)
            check_conversion(base_url, "Unsigned token", 401, unsigned)
            if not {"/.well-known/openid-configuration", "/keys"}.issubset(issuer.requested_paths):
                raise RuntimeError("The server did not retrieve both HTTPS discovery and JWKS documents")
            run("docker", "stop", "--time", "30", container, timeout=40)
            if run("docker", "inspect", "--format", "{{.State.ExitCode}}", container).strip() != b"0":
                raise RuntimeError("The JWT container did not stop cleanly")
            print(f"NativeAOT JWT smoke test passed: {arguments.image}", flush=True)
        except (RuntimeError, OSError, ValueError, subprocess.TimeoutExpired) as error:
            print(f"::error::{error}", file=sys.stderr)
            if started_container:
                sanitized_logs(container, tokens)
            return 1
        finally:
            if requested_container:
                subprocess.run(["docker", "rm", "--force", container], stdout=subprocess.DEVNULL,
                               stderr=subprocess.DEVNULL, timeout=30, check=False)
            if issuer is not None:
                issuer.shutdown()
                issuer.server_close()
            if thread is not None:
                thread.join(timeout=5)
    return 0


if __name__ == "__main__":
    sys.exit(main())
