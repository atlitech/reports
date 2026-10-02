#!/usr/bin/env python3
"""Exercise NativeAOT gateway/worker integration, not sandbox containment.

Usage: smoke-test-worker-gateway-aot.py SERVER_IMAGE WORKER_IMAGE
The explicitly enabled development Process backend runs the worker inside the server container.
Only a copied native worker executable is mounted read-only; neither process gets a Docker socket.
Credentials stay in memory and an owner-only settings file. No document, credentials, process
environments, or arbitrary subprocess/container logs are printed, including on failure.
"""

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


def run(*arguments, timeout=30):
    result = subprocess.run(arguments, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            timeout=timeout, check=False)
    if result.returncode:
        raise RuntimeError(f"{arguments[0]} {arguments[1]} failed (exit {result.returncode}); output omitted")
    return result.stdout


def remove_owned(name):
    result = subprocess.run(["docker", "rm", "--force", name], stdout=subprocess.DEVNULL,
                            stderr=subprocess.DEVNULL, timeout=30, check=False)
    if result.returncode:
        remaining = run("docker", "ps", "--all", "--filter", "name=^/" + name + "$",
                        "--format", "{{.ID}}")
        if remaining.strip():
            raise RuntimeError("An owned smoke-test container could not be removed")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *_arguments, **_keywords):
        return None


def request(url, credential=None, html=None):
    headers, body = {}, None
    if credential is not None:
        headers["X-Reports-Api-Key"] = credential
    if html is not None:
        headers["Content-Type"] = "application/json"
        body = json.dumps({"html": html, "options": {"paperSize": "A4"}}).encode()
    outgoing = urllib.request.Request(url, data=body, headers=headers)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        response = opener.open(outgoing, timeout=90 if html is not None else 5)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        result = response.read(1024 * 1024 + 1)
        if len(result) > 1024 * 1024:
            raise RuntimeError("Smoke-test response exceeded its size budget")
        return response.status, response.headers.get("Content-Type", ""), result


def assert_no_render_processes(container):
    # /proc/comm contains only process names, never document content or environment values.
    # A successful shell exit is the positive control; a missing/unreadable live procfs fails.
    run("docker", "exec", container, "/bin/sh", "-c", """
      set -eu
      test -r /proc/1/comm
      for entry in /proc/[0-9]*/comm; do
        name=$(cat "$entry" 2>/dev/null) || continue
        case "$name" in chrome*|headless*|Atli.Reports.Wo*) exit 1 ;; esac
      done
    """)


def wait_for_cleanup(container):
    deadline = time.monotonic() + 10
    while True:
        try:
            assert_no_render_processes(container)
            return
        except RuntimeError:
            if time.monotonic() >= deadline:
                raise RuntimeError("A render process remained after the completed worker job") from None
            time.sleep(0.1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("server_image")
    parser.add_argument("worker_image")
    arguments = parser.parse_args()
    os.umask(0o077)
    suffix = secrets.token_hex(8)
    copy_container = "atli-aot-worker-copy-" + suffix
    container = "atli-aot-worker-gateway-" + suffix
    copy_requested = server_requested = False
    with tempfile.TemporaryDirectory(prefix="atli-worker-gateway-aot-") as directory:
        work = Path(directory)
        try:
            # The extraction container never starts. The bind mount contains only this executable.
            binary_directory = work / "worker"
            binary_directory.mkdir(mode=0o755)
            binary_directory.chmod(0o755)
            copy_requested = True
            run("docker", "create", "--name", copy_container, arguments.worker_image)
            binary = binary_directory / "Atli.Reports.Worker"
            run("docker", "cp", copy_container + ":/app/Atli.Reports.Worker", str(binary))
            binary.chmod(0o755)
            remove_owned(copy_container)
            copy_requested = False

            credential = "aot-worker." + secrets.token_hex(32)
            verifier = base64.b64encode(hashlib.sha256(credential.encode()).digest()).decode()
            settings = {
                "ReportsServer__Authentication__Mode": "ApiKey",
                "ReportsServer__Authentication__ApiKeys__0__Id": "aot-worker",
                "ReportsServer__Authentication__ApiKeys__0__Hash": verifier,
                "ReportsServer__Authentication__ApiKeys__0__CallerId": "aot-worker",
                "ReportsServer__Authentication__ApiKeys__0__Permissions__0": "reports.convert",
                "ReportsServer__Authentication__ApiKeys__0__Permissions__1": "reports.diagnostics",
                "ReportsServer__Execution__Mode": "Worker",
                "ReportsServer__Execution__Backend": "Process",
                "ReportsServer__Execution__AllowDevelopmentProcess": "true",
                "ReportsServer__Execution__ProcessExecutablePath": "/worker/Atli.Reports.Worker",
                "ReportsServer__Execution__MaxConcurrentJobs": "1",
                "ReportsServer__Execution__Timeout": "00:01:00",
                "ReportsServer__Execution__CleanupTimeout": "00:00:15",
                "ReportsServer__Execution__EnvironmentVariables__ATLI_WORKER_BROWSER_PATH": "/opt/chrome-headless-shell/chrome-headless-shell",
                "ReportsServer__Execution__EnvironmentVariables__ATLI_WORKER_NO_SANDBOX": "true",
                "ReportsServer__Execution__EnvironmentVariables__ATLI_WORKER_DISABLE_DEV_SHM_USAGE": "true",
                "ReportsEngine__Browser__WarmUpOnStartup": "true",
                "ReportsEngine__Network__Mode": "Disabled",
            }
            envfile = work / "gateway.env"
            envfile.write_text("".join(f"{key}={value}\n" for key, value in settings.items()), encoding="ascii")
            envfile.chmod(0o600)
            server_requested = True
            run("docker", "run", "--detach", "--name", container,
                "--publish", "127.0.0.1::8080", "--env-file", str(envfile),
                "--mount", f"type=bind,source={binary_directory},target=/worker,readonly",
                "--user", "1654:1654", "--read-only", "--tmpfs", "/tmp:rw,nosuid,nodev,size=512m,mode=1777",
                "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
                "--memory", "1073741824", "--cpus", "1", "--pids-limit", "256", arguments.server_image)
            address = run("docker", "port", container, "8080/tcp").decode().splitlines()[0]
            base_url = "http://" + address
            deadline = time.monotonic() + 60
            while True:
                try:
                    status, _, body = request(base_url + "/health/ready")
                    if status == 200:
                        if set(json.loads(body)) != {"status"}:
                            raise RuntimeError("Anonymous readiness exposed detailed diagnostics")
                        break
                except (urllib.error.URLError, TimeoutError, ConnectionError):
                    pass
                if time.monotonic() >= deadline:
                    raise RuntimeError("The NativeAOT worker gateway did not become ready")
                if run("docker", "inspect", "--format", "{{.State.Running}}", container).strip() != b"true":
                    raise RuntimeError("The NativeAOT worker gateway exited during startup; logs omitted")
                time.sleep(0.2)

            run("docker", "exec", container, "/bin/sh", "-c",
                "test -x /app/Atli.Reports.Server && test ! -e /app/Atli.Reports.Server.dll && test ! -e /var/run/docker.sock")
            assert_no_render_processes(container)
            for endpoint, html in (("/health/details", None), ("/convert", "<h1>Unauthorized</h1>")):
                status, _, body = request(base_url + endpoint, html=html)
                if status != 401 or json.loads(body).get("kind") != "Unauthorized":
                    raise RuntimeError("The NativeAOT gateway did not enforce authentication")
            assert_no_render_processes(container)
            status, _, body = request(base_url + "/health/details", credential)
            checks = json.loads(body).get("checks", {})
            if status != 200 or set(checks) != {"worker_execution"} or checks["worker_execution"]["status"] != "Healthy":
                raise RuntimeError("NativeAOT worker readiness did not use the worker-only health registration")
            print("NativeAOT worker gateway: authentication and worker-only readiness passed", flush=True)

            for number in range(2):
                status, media_type, pdf = request(base_url + "/convert", credential,
                    f"<!doctype html><html><body><h1>NativeAOT worker job {number + 1}</h1></body></html>")
                if status != 200 or not media_type.startswith("application/pdf") or not pdf.startswith(b"%PDF-") or not pdf.rstrip().endswith(b"%%EOF"):
                    raise RuntimeError("NativeAOT worker conversion did not return a complete PDF")
                wait_for_cleanup(container)
                print(f"NativeAOT worker job {number + 1}: complete PDF and process cleanup passed", flush=True)
            run("docker", "stop", "--time", "20", container, timeout=30)
            if run("docker", "inspect", "--format", "{{.State.ExitCode}}", container).strip() != b"0":
                raise RuntimeError("The NativeAOT gateway did not shut down cleanly")
            print("NativeAOT development Process backend smoke passed; this is not a containment test", flush=True)
        finally:
            try:
                if server_requested:
                    remove_owned(container)
            finally:
                if copy_requested:
                    remove_owned(copy_container)


if __name__ == "__main__":
    main()
