#!/usr/bin/env python3
"""Runs the server image as a per-customer renderer on Azure Container Apps Sandboxes and measures it.

Everything runs in a resource group of its own, which the script creates and deletes (unless
--keep): a sandbox group, a disk image built from this commit's server Dockerfile, and the
sandboxes below. Nothing else in the subscription is read or changed.

  1. Builds the disk image remotely from the server Dockerfile (aca sandboxgroup disk create
     --source), from the commit's tracked files only.
  2. Starts a renderer (2 vCPU, 4 GiB) with deny-by-default egress and the server image's own
     entrypoint, exposes port 8080, and waits for /health/ready.
  3. Inside the renderer: Chromium's sandbox (chromium-sandbox-check.sh), the renderer processes'
     own seccomp filter, seccomp-probe.pl (expected to report no container-level filter: the
     microVM is the boundary), warm conversion times measured in the VM, and the VM's memory peak
     during the 49-page report.
  4. From a second sandbox in the same region (the API's position): the same conversions through
     the platform's port proxy.
  5. Cold starts: new renderers from create to /health/ready.
  6. Suspend and resume, a request to a suspended renderer, and auto-suspend after idling.
     Creating, exposing, stopping, and resuming renderers go through the data-plane REST API with
     one cached token, as an API would call it; every aca command fetches a token of its own.
  7. A memory snapshot of the warm renderer, two sandboxes started from it, and whether they share
     the browser's memory layout (ASLR) and environment with the original.
  8. Egress and identity from inside the renderer (network-probe.sh) and the egress decision log.
  9. The same warm conversions at 1 vCPU / 2 GiB and 0.5 vCPU / 1 GiB.

Requirements: az (logged in, with rights to create a resource group), aca
(https://aka.ms/aca-cli-install), git, and openssl. The port is exposed anonymously for the run;
the server still requires its API key, generated for this run and never printed.

  benchmarks/azure-sandboxes/run.py [--location eastus2] [--samples 5] [--output results.json] [--keep]
"""
import argparse
import json
import os
import re
import shutil
import statistics
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
UUID = r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
FIXTURES = ["invoice", "chart", "long-table"]
TIERS = {"L": ("2000m", "4096Mi"), "M": ("1000m", "2048Mi"), "S": ("500m", "1024Mi")}
ENTRYPOINT = ["/usr/bin/tini", "--", "/app/Atli.Reports.Server"]
API_VERSION = "2026-02-01-preview"
# Consumption-plan rates that Sandboxes are billed at (Azure retail prices API, eastus2,
# 2026-10-03), and Premium Blob ZRS storage for snapshots.
VCPU_SECOND = 0.000024
GIB_SECOND = 0.000003
SNAPSHOT_GB_MONTH = 0.20


def log(message):
    print(f"[{time.strftime('%H:%M:%S')}] {message}", flush=True)


def run(command, check=True, env=None, timeout=1800):
    result = subprocess.run(
        command, capture_output=True, text=True, env=env, timeout=timeout, check=False
    )
    if check and result.returncode != 0:
        raise RuntimeError(
            f"{' '.join(command[:4])} ... exited {result.returncode}:\n{result.stderr[-2000:]}"
        )
    return result


class Aca:
    def __init__(self, subscription, resource_group, group, region):
        self.env = dict(
            os.environ,
            ACA_SUBSCRIPTION=subscription,
            ACA_RESOURCE_GROUP=resource_group,
            ACA_SANDBOX_GROUP=group,
            ACA_REGION=region,
        )

    def __call__(self, *args, check=True, timeout=1800):
        return run(["aca", *args], check=check, env=self.env, timeout=timeout)

    def create(self, *args):
        started = time.monotonic()
        output = self("sandbox", "create", *args, "-o", "json").stdout
        seconds = time.monotonic() - started
        match = re.search(rf"Created sandbox: ({UUID})", output)
        if not match:
            raise RuntimeError(f"No sandbox ID in: {output}")
        return match.group(1), seconds

    def exec(self, sandbox, command, check=True, timeout=900):
        return self("sandbox", "exec", "--id", sandbox, "-c", command, check=check, timeout=timeout)

    def write(self, sandbox, path, file):
        self("sandbox", "fs", "write", "--id", sandbox, "--path", path, "--file", str(file))

    def timed(self, *args):
        started = time.monotonic()
        self(*args)
        return time.monotonic() - started


class Rest:
    """The Sandboxes data plane over HTTPS with one cached token, the way an API would call it. Each
    aca command fetches its own token, which adds seconds; lifecycle timings use this instead."""

    def __init__(self, subscription, resource_group, group, region):
        self.base = (
            f"https://management.{region}.azuredevcompute.io/subscriptions/{subscription}"
            f"/resourceGroups/{resource_group}/sandboxGroups/{group}"
        )
        self.token = run(["az", "account", "get-access-token", "--resource", "https://dynamicsessions.io",
                          "--query", "accessToken", "-o", "tsv"]).stdout.strip()

    def call(self, method, path, body=None):
        request = urllib.request.Request(
            f"{self.base}/{path}?api-version={API_VERSION}", method=method,
            data=None if body is None else json.dumps(body).encode(),
            headers={"Authorization": f"Bearer {self.token}", "Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=600) as response:
            return json.loads(response.read() or b"{}")

    def timed(self, method, path, body=None):
        started = time.monotonic()
        result = self.call(method, path, body)
        return result, time.monotonic() - started

    def wait_state(self, sandbox, state, started, limit=900):
        while self.call("GET", f"sandboxes/{sandbox}").get("state") != state:
            if time.monotonic() - started > limit:
                raise RuntimeError(f"{sandbox} did not reach {state} within {limit} s")
            time.sleep(0.1)
        return time.monotonic() - started

    def create(self, disk, server_env, tier="L", label="renderer"):
        cpu, memory = TIERS[tier]
        body = {
            "sourcesRef": {"diskImage": {"id": disk, "isPublic": False}},
            "resources": {"cpu": cpu, "memory": memory},
            "egressPolicy": {"defaultAction": "Deny"},
            "entrypoint": ENTRYPOINT,
            "environment": dict(line.split("=", 1) for line in server_env),
            "labels": {"role": label},
        }
        sandbox, seconds = self.timed("PUT", "sandboxes", body)
        return sandbox["id"], seconds

    def expose(self, sandbox):
        sandbox_view, seconds = self.timed(
            "POST", f"sandboxes/{sandbox}/ports/add", {"port": 8080, "auth": {"anonymous": True}})
        url = next(port["url"] for port in sandbox_view["ports"] if port["port"] == 8080)
        return url, seconds


def http(url, data=None, headers=None, timeout=60):
    request = urllib.request.Request(url, data=data, headers=headers or {})
    started = time.monotonic()
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read()
            status = response.status
    except urllib.error.HTTPError as error:
        body, status = error.read(), error.code
    except (urllib.error.URLError, TimeoutError, ConnectionError):
        body, status = b"", 0
    return status, time.monotonic() - started, body


def wait_ready(url, started, limit=180):
    while time.monotonic() - started < limit:
        if http(f"{url}/health/ready", timeout=5)[0] == 200:
            return time.monotonic() - started
        time.sleep(0.05)
    raise RuntimeError(f"{url} was not ready within {limit} s")


def median(values):
    return round(statistics.median(values), 4) if values else None


def parse_client_lines(output):
    """Lines of convert-client.pl or the curl loop: '<label> <status> <seconds> <bytes> ...'."""
    samples = {}
    for line in output.splitlines():
        parts = line.split()
        if len(parts) >= 4 and parts[1].isdigit():
            samples.setdefault(parts[0], []).append((int(parts[1]), float(parts[2]), int(parts[3])))
    return samples


def summarize(samples, warmups=1):
    summary = {}
    for fixture, values in samples.items():
        measured = values[warmups:]
        summary[fixture] = {
            "statuses": sorted({status for status, _, _ in values}),
            "first_s": round(values[0][1], 4),
            "median_s": median([seconds for _, seconds, _ in measured]),
            "min_s": round(min(seconds for _, seconds, _ in measured), 4) if measured else None,
            "max_s": round(max(seconds for _, seconds, _ in measured), 4) if measured else None,
            "samples": len(measured),
            "bytes": values[-1][2],
        }
    return summary


def in_vm_timings(aca, sandbox, samples):
    script = (
        "cd /tmp/spike; for f in " + " ".join(FIXTURES) + "; do "
        f"for i in $(seq 0 {samples}); do printf '%s ' $f; perl convert-client.pl $f.json api-key; "
        "done; done"
    )
    # check=False: a tier too small for a fixture should be recorded, not end the run.
    return summarize(parse_client_lines(aca.exec(sandbox, script, check=False).stdout))


def memory_peak(aca, sandbox):
    """MemTotal - MemAvailable of the whole VM, sampled every 50 ms during the 49-page report."""
    script = (
        'cd /tmp/spike; mem() { awk "/MemTotal/{t=\\$2}/MemAvailable/{a=\\$2}END{print int((t-a)/1024)}" /proc/meminfo; }; '
        'before=$(mem); peak=$before; perl convert-client.pl long-table.json api-key > /dev/shm/result & pid=$!; '
        'while kill -0 $pid 2>/dev/null; do m=$(mem); [ $m -gt $peak ] && peak=$m; sleep 0.05; done; '
        'echo "before=$before peak=$peak total=$(awk "/MemTotal/{print int(\\$2/1024)}" /proc/meminfo) result=$(tr " " _ < /dev/shm/result)"'
    )
    output = aca.exec(sandbox, script, check=False).stdout
    return dict(pair.split("=", 1) for pair in output.split() if "=" in pair)


def upload_helpers(aca, sandbox, work):
    for name in ["convert-client.pl", "chromium-sandbox-check.sh", "network-probe.sh"]:
        aca.write(sandbox, f"/tmp/spike/{name}", HERE / name)
    aca.write(sandbox, "/tmp/spike/seccomp-probe.pl", REPO / ".github/scripts/seccomp-probe.pl")
    for fixture in FIXTURES:
        aca.write(sandbox, f"/tmp/spike/{fixture}.json", work / f"{fixture}.json")
    aca.write(sandbox, "/tmp/spike/api-key", work / "api-key")


def write_requests(work, credentials):
    # The load benchmark's settings: A4, 0.4 in margins, backgrounds, tagged PDFs.
    for fixture in FIXTURES:
        options = {
            "paperSize": "a4",
            "margins": {"top": 0.4, "bottom": 0.4, "left": 0.4, "right": 0.4},
            "printBackground": True,
            "generateTaggedPdf": True,
        }
        if fixture == "chart":
            options["waitForSignal"] = "reportReady"
        html = (REPO / "benchmarks/fixtures" / f"{fixture}.html").read_text()
        (work / f"{fixture}.json").write_text(json.dumps({"html": html, "options": options}))
    key = re.search(r"^ReportsClient__ApiKey=(.+)$", (credentials / "client.env").read_text(), re.M)
    (work / "api-key").write_text(key.group(1))
    (work / "api-key").chmod(0o600)


def build_context(work):
    context = work / "context"
    context.mkdir()
    paths = ["global.json", "Directory.Build.props", "Directory.Packages.props", ".editorconfig",
             "src/Atli.Reports.Engine", "src/Atli.Reports.Client", "src/Atli.Reports.Hosting",
             "src/Atli.Reports.Server"]
    archive = subprocess.run(["git", "-C", str(REPO), "archive", "HEAD", *paths],
                             capture_output=True, check=True).stdout
    subprocess.run(["tar", "-x", "-C", str(context)], input=archive, check=True)
    shutil.copy(context / "src/Atli.Reports.Server/Dockerfile", context / "Dockerfile")
    return context


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--location", default="eastus2")
    parser.add_argument("--samples", type=int, default=5, help="measured samples after one warmup")
    parser.add_argument("--cold-starts", type=int, default=3)
    parser.add_argument("--cycles", type=int, default=3, help="suspend/resume cycles")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--keep", action="store_true", help="keep the resource group")
    options = parser.parse_args()

    for tool in ["az", "aca", "git", "openssl"]:
        if not shutil.which(tool):
            sys.exit(f"Missing required tool: {tool}")
    account = json.loads(run(["az", "account", "show", "-o", "json"]).stdout)
    commit = run(["git", "-C", str(REPO), "rev-parse", "--short", "HEAD"]).stdout.strip()
    stamp = time.strftime("%Y%m%d%H%M%S", time.gmtime())
    resource_group = f"rg-atli-reports-sandboxes-{stamp}"
    group = f"reports-sandboxes-{stamp}"
    aca = Aca(account["id"], resource_group, group, options.location)
    results = {
        "commit": commit,
        "dirty": bool(run(["git", "-C", str(REPO), "status", "--porcelain"]).stdout.strip()),
        "location": options.location,
        "aca_cli": aca("--version").stdout.strip(),
        "started_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "samples": options.samples,
    }

    work = Path(tempfile.mkdtemp(prefix="atli-sandboxes-"))
    created_group = False
    try:
        log(f"Resource group {resource_group} in {options.location}")
        run(["az", "group", "create", "--name", resource_group, "--location", options.location,
             "--tags", "purpose=atli-reports-sandboxes-spike", f"commit={commit}", "-o", "none"])
        created_group = True
        aca("sandboxgroup", "create", "--name", group, "--location", options.location)
        # The group's create grants the caller its data-plane role, which takes a few minutes to
        # propagate; until then data-plane calls answer 403.
        started = time.monotonic()
        while aca("sandbox", "list", "-o", "json", check=False).returncode != 0:
            if time.monotonic() - started > 900:
                raise RuntimeError("The data-plane role did not propagate within 15 minutes.")
            time.sleep(15)
        results["role_propagation_s"] = round(time.monotonic() - started, 1)

        run(["bash", str(REPO / "scripts/create-reports-api-key.sh"), str(work / "credentials"),
             "sandboxes-spike"])
        server_env = (work / "credentials/server.env").read_text().split()
        write_requests(work, work / "credentials")

        log("Building the disk image from the server Dockerfile")
        context = build_context(work)
        for attempt in range(6):
            started = time.monotonic()
            build = aca("sandboxgroup", "disk", "create", "--source", str(context),
                        "--name", f"reports-server-{commit}", "--wait-timeout", "3000", check=False)
            if build.returncode == 0:
                break
            # The content-package upload can still answer 403 shortly after the role works for
            # sandbox calls.
            if "403" not in build.stderr or attempt == 5:
                raise RuntimeError(f"Disk image build failed:\n{build.stderr[-2000:]}")
            time.sleep(30)
        results["disk_build_s"] = round(time.monotonic() - started, 1)
        disks = json.loads(aca("sandboxgroup", "disk", "list", "-o", "json").stdout)
        disk = next(d["id"] for d in disks if d.get("name") == f"reports-server-{commit}")

        log("Renderer (2 vCPU, 4 GiB)")
        rest = Rest(account["id"], resource_group, group, options.location)
        started = time.monotonic()
        renderer, create_s = rest.create(disk, server_env)
        url, port_s = rest.expose(renderer)
        ready_s = wait_ready(url, started)
        results["renderer"] = {"create_call_s": round(create_s, 2), "port_call_s": round(port_s, 2),
                               "ready_after_create_s": round(ready_s, 2)}
        results["rest_get_s"] = median(
            [rest.timed("GET", f"sandboxes/{renderer}")[1] for _ in range(5)])
        results["cli_get_s"] = median(
            [aca.timed("sandbox", "get", "--id", renderer, "-o", "json") for _ in range(3)])
        results["laptop_rtt_health_s"] = median(
            [http(f"{url}/health/live")[1] for _ in range(5)])
        status, _, _ = http(f"{url}/convert", data=b'{"html":"<p>x</p>"}',
                            headers={"Content-Type": "application/json"})
        results["anonymous_convert_status"] = status

        upload_helpers(aca, renderer, work)
        info = aca.exec(renderer, "uname -r; uname -m; nproc; grep -m1 'model name' /proc/cpuinfo | cut -d: -f2; "
                        "grep MemTotal /proc/meminfo; id -u; grep Seccomp: /proc/self/status").stdout
        results["vm"] = [line.strip() for line in info.splitlines()]
        check = aca.exec(renderer, "sh /tmp/spike/chromium-sandbox-check.sh", check=False)
        results["chromium_sandbox"] = {"exit": check.returncode, "output": check.stdout.strip()}
        renderers = aca.exec(renderer, (
            "cd /tmp/spike; perl convert-client.pl long-table.json api-key > /dev/null & sleep 1; "
            "for p in $(pgrep -f -- --type=renderer); do grep Seccomp: /proc/$p/status | tr -d '\\t'; done; wait"
        )).stdout
        results["renderer_process_seccomp"] = sorted(set(renderers.split()))
        probe = aca.exec(renderer, "perl /tmp/spike/seccomp-probe.pl chromium", check=False)
        results["seccomp_probe"] = {"exit": probe.returncode, "lines": probe.stdout.splitlines()}

        log("Warm conversions inside the VM")
        results["in_vm_L"] = in_vm_timings(aca, renderer, options.samples)
        results["memory_L_mib"] = memory_peak(aca, renderer)

        log("Client sandbox in the same region")
        client, _ = aca.create("--disk", "ubuntu", "--cpu", "1000m", "--memory", "2048Mi",
                               "--egress-default", "Deny", "--egress-rule", "*.adcproxy.io:Allow",
                               "--label", "role=client")
        for fixture in FIXTURES:
            aca.write(client, f"/root/spike/{fixture}.json", work / f"{fixture}.json")
        aca.write(client, "/root/spike/client.curl", work / "credentials/client.curl")
        curl_loop = (
            "cd /root/spike; for f in " + " ".join(FIXTURES) + "; do "
            f"for i in $(seq 0 {options.samples}); do curl -s -o /dev/null -m 120 --config client.curl "
            "-H 'Content-Type: application/json' --data-binary @$f.json "
            f"-w \"$f %{{http_code}} %{{time_total}} %{{size_download}}\\n\" {url}/convert; done; done; "
            f"for i in $(seq 0 {options.samples}); do curl -s -o /dev/null "
            f"-w 'health %{{http_code}} %{{time_total}} 0\\n' {url}/health/live; done"
        )
        results["via_proxy_L"] = summarize(parse_client_lines(aca.exec(client, curl_loop).stdout))

        log("Cold starts")
        cold = []
        invoice = (work / "invoice.json").read_bytes()
        api_headers = {"Content-Type": "application/json",
                       "X-Reports-Api-Key": (work / "api-key").read_text()}
        for _ in range(options.cold_starts):
            started = time.monotonic()
            sandbox, create_s = rest.create(disk, server_env, label="cold")
            cold_url, port_s = rest.expose(sandbox)
            ready_s = wait_ready(cold_url, started)
            first = http(f"{cold_url}/convert", data=invoice, headers=api_headers)
            cold.append({"create_call_s": round(create_s, 2), "port_call_s": round(port_s, 2),
                         "ready_after_create_s": round(ready_s, 2), "first_invoice_status": first[0],
                         "first_invoice_from_laptop_s": round(first[1], 3)})
            rest.call("DELETE", f"sandboxes/{sandbox}")
        results["cold_starts"] = cold

        log("Suspend and resume")
        cycles = []
        invoice_via_proxy = (
            f"cd /root/spike; curl -s -o /dev/null -m 120 --config client.curl -H 'Content-Type: application/json' "
            f"--data-binary @invoice.json -w 'invoice %{{http_code}} %{{time_total}} %{{size_download}}\\n' {url}/convert"
        )
        for cycle in range(options.cycles):
            started = time.monotonic()
            _, stop_call_s = rest.timed("POST", f"sandboxes/{renderer}/stop", {})
            entry = {"stop_call_s": round(stop_call_s, 2),
                     "stopped_after_s": round(rest.wait_state(renderer, "Stopped", started), 2)}
            if cycle == 0:
                status, _, body = http(f"{url}/health/live")
                entry["request_while_stopped"] = f"{status} {body.decode(errors='replace')}"
            # What a caller sees: resume, then the first conversion, measured from the laptop.
            started = time.monotonic()
            _, resume_call_s = rest.timed("POST", f"sandboxes/{renderer}/resume", {})
            statuses = []
            while not statuses or statuses[-1] != 200:
                if time.monotonic() - started > 60:
                    raise RuntimeError(f"No PDF within 60 s of resuming: {statuses}")
                statuses.append(http(f"{url}/convert", data=invoice, headers=api_headers)[0])
            entry.update({
                "resume_call_s": round(resume_call_s, 2),
                "statuses_until_pdf": statuses,
                "resume_to_first_pdf_from_laptop_s": round(time.monotonic() - started, 3),
                "second_invoice_from_laptop_s": round(http(f"{url}/convert", data=invoice, headers=api_headers)[1], 3),
            })
            cycles.append(entry)
        results["suspend_resume"] = cycles

        log("Snapshot and clones")
        started = time.monotonic()
        aca("sandbox", "snapshot", "--id", renderer, "--name", "warm-renderer")
        snapshot_s = time.monotonic() - started
        snapshots = json.loads(aca("sandboxgroup", "snapshot", "list", "-o", "json").stdout)
        snapshot = next(s for s in snapshots if s.get("labels", {}).get("name") == "warm-renderer")
        layout = (
            'b=$(pgrep -f remote-debugging-port | head -1); '
            'echo "binary=$(grep -m1 chrome-headless-shell /proc/$b/maps | cut -d- -f1) '
            'libc=$(grep -m1 libc.so /proc/$b/maps | cut -d- -f1) '
            'stack=$(grep "\\[stack\\]" /proc/$b/maps | cut -d- -f1) '
            'boot_id=$(cat /proc/sys/kernel/random/boot_id) '
            'urandom=$(head -c 8 /dev/urandom | od -An -tx1 | tr -d " \\n") '
            'key_id=$ReportsServer__Authentication__ApiKeys__0__Id"'
        )
        views = {"original": dict(p.split("=", 1) for p in aca.exec(renderer, layout).stdout.split())}
        clone_create = []
        for index in (1, 2):
            clone, seconds = aca.create("--snapshot", "warm-renderer", "--label", "role=clone")
            clone_create.append(round(seconds, 2))
            views[f"clone{index}"] = dict(p.split("=", 1) for p in aca.exec(clone, layout).stdout.split())
        same = lambda key: len({view[key] for view in views.values()}) == 1  # noqa: E731
        results["snapshot"] = {
            "snapshot_s": round(snapshot_s, 2),
            "size_mb": snapshot.get("sizeInMB"),
            "vmm": snapshot.get("vmmType"),
            "clone_create_s": clone_create,
            "same_browser_layout": same("binary") and same("libc") and same("stack"),
            "same_boot_id": same("boot_id"),
            "same_urandom": same("urandom"),
            "same_api_key_id": same("key_id"),
        }

        log("Egress and identity")
        probe = aca.exec(renderer, "bash /tmp/spike/network-probe.sh").stdout
        results["network"] = dict(
            pair.split("=", 1) for pair in probe.split() if "=" in pair)
        decisions = json.loads(aca("sandbox", "egress", "decisions", "--id", renderer, "-o", "json").stdout)
        denied = decisions.get("networkEgress", {}).get("denied", [])
        results["egress_decisions_denied_hosts"] = sorted({entry.get("host") for entry in denied})

        log("Auto-suspend after 60 s idle")
        aca("sandbox", "lifecycle", "set", "--id", renderer, "--auto-suspend", "enable",
            "--idle-timeout-seconds", "60", "--mode", "Memory")
        aca.exec(client, invoice_via_proxy)
        last_request = time.monotonic()
        while (rest.call("GET", f"sandboxes/{renderer}").get("state") != "Stopped"
               and time.monotonic() - last_request < 360):
            time.sleep(2)
        results["auto_suspend_observed_after_s"] = round(time.monotonic() - last_request, 1)

        for tier in ["M", "S"]:
            log(f"Tier {tier} {TIERS[tier]}")
            started = time.monotonic()
            sandbox, _ = rest.create(disk, server_env, tier=tier, label=f"tier-{tier}")
            tier_url, _ = rest.expose(sandbox)
            ready_s = wait_ready(tier_url, started)
            upload_helpers(aca, sandbox, work)
            results[f"in_vm_{tier}"] = in_vm_timings(aca, sandbox, options.samples)
            results[f"memory_{tier}_mib"] = memory_peak(aca, sandbox)
            results[f"ready_after_create_{tier}_s"] = round(ready_s, 2)
            aca("sandbox", "delete", "--id", sandbox, "--yes")

        hour = 3600
        results["cost_per_running_hour_usd"] = {
            tier: round(hour * (float(cpu[:-1]) / 1000 * VCPU_SECOND + float(mem[:-2]) / 1024 * GIB_SECOND), 4)
            for tier, (cpu, mem) in TIERS.items()
        }
        results["snapshot_storage_usd_month"] = round((snapshot.get("sizeInMB") or 0) / 1000 * SNAPSHOT_GB_MONTH, 4)
    finally:
        results["finished_utc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        output = json.dumps(results, indent=2)
        if options.output:
            options.output.write_text(output + "\n")
        print(output)
        shutil.rmtree(work, ignore_errors=True)
        if created_group and not options.keep:
            log(f"Deleting {resource_group}")
            aca("sandboxgroup", "delete", "--name", group, "--yes", check=False)
            run(["az", "group", "delete", "--name", resource_group, "--yes"], check=False, timeout=3600)
            log("Deleted")


if __name__ == "__main__":
    main()
