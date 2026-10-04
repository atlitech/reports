#!/usr/bin/env python3
"""What a principal holding only sandboxes/read and sandboxes/resume/action can do to a renderer.

Runs inside a sandbox whose sandbox group has a user-assigned managed identity (the "tester"), with
egress allowed to the data plane. It takes a token for the Sandboxes data plane from the platform's
managed-identity endpoint and calls the renderer group's REST API, printing one JSON line per call:
{"call": ..., "status": ...}. It never prints a token. followup.py uploads and runs it.

  python3 role-probe.py <phase> <renderer-group-base-url> <sandbox-id> <identity-client-id> [port-url]

Phases:
  denied  every call the role must refuse (and the reads it must allow), while the sandbox runs
  resume  resume the stopped sandbox, then read its state
  port    a renderer port gated by Entra ID, called with the identity's bearer tokens
"""
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

API_VERSION = "2026-02-01-preview"


def identity_token(resource, client_id):
    """A token from the sandbox's managed-identity endpoint (App Service style, as network-probe.sh)."""
    query = urllib.parse.urlencode({"api-version": "2019-08-01", "resource": resource, "client_id": client_id})
    request = urllib.request.Request(f"{os.environ['IDENTITY_ENDPOINT']}?{query}",
                                     headers={"X-IDENTITY-HEADER": os.environ["IDENTITY_HEADER"]})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read())["access_token"], None
    except urllib.error.HTTPError as error:
        return None, f"{error.code} {error.read()[:200].decode(errors='replace')}"


def call(token, method, url, body=None, content_type="application/json", raw=None):
    data = raw if raw is not None else (None if body is None else json.dumps(body).encode())
    request = urllib.request.Request(url, method=method, data=data,
                                     headers={"Authorization": f"Bearer {token}", "Content-Type": content_type})
    started = time.monotonic()
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            status, text = response.status, response.read()[:300]
    except urllib.error.HTTPError as error:
        status, text = error.code, error.read()[:300]
    except (urllib.error.URLError, TimeoutError) as error:
        status, text = 0, str(error).encode()
    return status, round(time.monotonic() - started, 3), text.decode(errors="replace")


def main():
    phase, base, sandbox, client_id = sys.argv[1:5]
    token, error = identity_token("https://dynamicsessions.io", client_id)
    if token is None:
        print(json.dumps({"call": "identity token for https://dynamicsessions.io", "status": error}))
        sys.exit(1)
    box = f"{base}/sandboxes/{sandbox}"
    api = f"api-version={API_VERSION}"

    def report(name, method, url, body=None, **kwargs):
        status, seconds, text = call(token, method, url, body, **kwargs)
        # Error bodies are short and name the missing permission; success bodies are not echoed.
        print(json.dumps({"call": name, "status": status, "seconds": seconds,
                          "detail": text[:200] if status >= 300 or status == 0 else ""}), flush=True)
        return status

    if phase == "denied":
        # Reads the role should allow. Retries cover role-assignment propagation.
        for attempt in range(60):
            if report("GET sandbox", "GET", f"{box}?{api}") == 200:
                break
            time.sleep(15)
        report("GET sandboxes (list)", "GET", f"{base}/sandboxes?{api}")
        # Everything else must be refused.
        report("GET ports", "GET", f"{box}/ports?{api}")
        report("POST stop", "POST", f"{box}/stop?{api}", {})
        report("POST executeShellCommand", "POST", f"{box}/executeShellCommand?{api}", {"command": "id"})
        report("POST executeCommand", "POST", f"{box}/executeCommand?{api}", {"command": ["id"]})
        report("GET files (read /etc/hostname)", "GET", f"{box}/files?path=/etc/hostname&{api}")
        report("GET files/list", "GET", f"{box}/files/list?path=/&{api}")
        report("PUT files (write /tmp/role-probe)", "PUT", f"{box}/files?path=/tmp/role-probe&createDirs=true&{api}",
               raw=b"x", content_type="application/octet-stream")
        report("POST egresspolicy (Allow all)", "POST", f"{box}/egresspolicy?{api}", {"defaultAction": "Allow"})
        report("POST lifecycle", "POST", f"{box}/lifecycle?{api}",
               {"autoSuspendPolicy": {"enabled": False, "interval": 0}})
        report("POST ports/add (anonymous 9999)", "POST", f"{box}/ports/add?{api}",
               {"port": 9999, "auth": {"anonymous": True}})
        report("POST snapshot", "POST", f"{box}/snapshot?{api}", {})
        report("POST commit", "POST", f"{box}/commit?{api}", {})
        report("POST disable", "POST", f"{box}/disable?{api}", {})
        report("GET stats", "GET", f"{box}/stats?{api}")
        report("GET egress-decisions", "GET", f"{box}/egress-decisions?{api}")
        report("PUT sandboxes (create)", "PUT", f"{base}/sandboxes?{api}",
               {"sourcesRef": {"diskImage": {"name": "ubuntu", "isPublic": True}},
                "resources": {"cpu": "250m", "memory": "512Mi"}, "labels": {"role": "role-probe"}})
        report("DELETE sandbox", "DELETE", f"{box}?{api}")
        report("GET secrets", "GET", f"{base}/secrets?{api}")
        report("GET diskimages", "GET", f"{base}/diskimages?{api}")
        report("GET snapshots", "GET", f"{base}/snapshots?{api}")
    elif phase == "resume":
        report("GET sandbox (before)", "GET", f"{box}?{api}")
        report("POST resume", "POST", f"{box}/resume?{api}", {})
        started = time.monotonic()
        state = None
        while state != "Running" and time.monotonic() - started < 120:
            request = urllib.request.Request(f"{box}?{api}", headers={"Authorization": f"Bearer {token}"})
            with urllib.request.urlopen(request, timeout=30) as response:
                state = json.loads(response.read()).get("state")
            time.sleep(0.5)
        print(json.dumps({"call": "state after resume", "status": state,
                          "seconds": round(time.monotonic() - started, 2)}), flush=True)
    elif phase == "port":
        port_url = sys.argv[5]
        for resource in ["https://dynamicsessions.io", "https://management.azure.com/",
                         "409cf302-c83f-43c3-94eb-ca581ab18c6d", port_url]:
            port_token, error = identity_token(resource, client_id)
            if port_token is None:
                print(json.dumps({"call": f"identity token for {resource}", "status": error}), flush=True)
                continue
            status, seconds, text = call(port_token, "GET", port_url + "/")
            print(json.dumps({"call": f"GET port with identity token for {resource}", "status": status,
                              "detail": text[:120]}), flush=True)


if __name__ == "__main__":
    main()
