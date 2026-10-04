#!/usr/bin/env python3
"""End-to-end test of the hosted renderer path on Azure Container Apps Sandboxes.

Everything runs in a resource group of its own, which `setup` creates and `cleanup` deletes:

  setup    The resource group (tagged purpose=atli-reports-hosted-e2e), a sandbox group, and two
           disk images built remotely from this commit's server Dockerfile (as run.py builds one):
           reports-server-<sha>-a for the tenants, reports-server-<sha>-b as the rollout target.
  run      The provisioner (src/Atli.Reports.Provisioner, built in Release and configured by
           Provisioner__* variables) creates tenant `acme` (OnDemand port) and tenant `globex`
           (Manual port), size S, auto-suspend after 60 s, the port admitting only this machine's
           public IPv4 address, records in a File store. The server, built the same way, runs on
           127.0.0.1 in gateway mode with an API-key caller, the same File store, and
           Wake:Mode=Sandboxes with the az login. Through the gateway:
             a. both tenants convert; a missing tenant header is 400, a tenant the caller lacks
                403, a wrong caller key 401;
             b. acme, stopped by auto-suspend, wakes on the request (OnDemand); the 49-page report
                right after a wake;
             c. globex, stopped by auto-suspend, wakes through the gateway's resume (Manual port),
                with events 60 and 61 in the gateway log;
             d. from a client sandbox in the region, acme's port refuses the request by source
                address (403 IpAccessDenied) without waking acme;
             e. a rollout to disk b while a loop converts for acme every 0.5 s: every status, the
                records on new sandboxes, the old ones deleted after the drain, prune finding none;
             f. disable acme: conversions fail with 503; enable: the next one wakes and succeeds;
             g. delete globex while the gateway holds its record: its conversions are 503, and
                list shows acme alone;
             h. acme's port re-added admitting only another range: the gateway answers 503 and
                logs the refusal (event 58).
           Then the provisioner deletes the remaining tenant, every log and the results are
           checked for secrets, and each check gets a verdict.
  cleanup  Deletes the sandbox group and the resource group, and waits until the resource group
           is gone.
  all      setup, run, and cleanup, cleaning up even when a step fails.

Secrets: the caller's API key and every renderer credential live only in --work (mode 0700),
which cleanup removes; nothing here prints them, this machine's public address, or the
subscription ID. The gateway's and provisioner's logs (in --work/logs) name tenants, sandboxes,
port URLs, and disk images only.

Requirements: az (logged in, with rights to create a resource group), aca
(https://aka.ms/aca-cli-install, on PATH or in --aca), the .NET SDK, git, openssl, and curl.

  benchmarks/azure-sandboxes/e2e.py all [--work DIR] [--output results.json] [--location eastus2]
  benchmarks/azure-sandboxes/e2e.py setup|run|cleanup --work DIR [--output results.json]
"""
import argparse
import concurrent.futures
import http.client
import json
import os
import re
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import run as spike  # noqa: E402  run.py's helpers: Aca, Rest, http, log, write_requests, build_context

REPO = spike.REPO
log = spike.log
TENANTS = ["acme", "globex"]
CALLER = "e2e-app"


# ---------------------------------------------------------------------------------------------
# State, Azure CLI, and the data plane
# ---------------------------------------------------------------------------------------------

class State(dict):
    """Resource names and IDs (never secrets), so the subcommands can run one at a time."""

    def __init__(self, path):
        super().__init__(json.loads(path.read_text()) if path.exists() else {})
        self.path = path

    def save(self, **values):
        self.update(values)
        self.path.write_text(json.dumps(self, indent=2) + "\n")
        self.path.chmod(0o600)


class Context:
    def __init__(self, options):
        self.options = options
        self.work = options.work
        self.work.mkdir(mode=0o700, parents=True, exist_ok=True)
        self.work.chmod(0o700)
        (self.work / "logs").mkdir(mode=0o700, exist_ok=True)
        self.state = State(self.work / "state.json")
        self.output = options.output
        self.results = json.loads(self.output.read_text()) if self.output and self.output.exists() else {}
        self.subscription = json.loads(spike.run(["az", "account", "show", "-o", "json"]).stdout)["id"]
        self.region = options.location

    def record(self, key, value):
        self.results[key] = value
        if self.output:
            self.output.parent.mkdir(parents=True, exist_ok=True)
            self.output.write_text(json.dumps(self.results, indent=2) + "\n")

    @property
    def resource_group(self):
        return self.state["resource_group"]

    @property
    def group(self):
        return self.state["sandbox_group"]

    @property
    def records(self):
        """The File record store the provisioner writes and the gateway reads."""
        return self.work / "records"

    def aca(self):
        return spike.Aca(self.subscription, self.resource_group, self.group, self.region)

    def rest(self):
        return Rest(self.subscription, self.resource_group, self.group, self.region)


class Rest(spike.Rest):
    """run.py's data-plane client, returning (status, body) instead of raising. Its token lasts
    about an hour; run() makes a new client after 30 minutes."""

    def request(self, method, path, body=None):
        url = f"{self.base}/{path}?api-version={spike.API_VERSION}"
        request = urllib.request.Request(url, method=method,
                                         data=None if body is None else json.dumps(body).encode(),
                                         headers={"Authorization": f"Bearer {self.token}",
                                                  "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                status, data = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, data = error.code, error.read()
        try:
            return status, json.loads(data or b"{}")
        except ValueError:
            return status, {"text": data[:300].decode(errors="replace")}

    def sandbox(self, sandbox):
        """The sandbox's view, or None when it does not exist (404). Other failures are retried a
        few times and then raised, so a transient error never reads as a deleted sandbox."""
        for attempt in range(4):
            status, view = self.request("GET", f"sandboxes/{sandbox}")
            if status == 200:
                return view
            if status == 404:
                return None
            time.sleep(2 ** attempt)
        raise RuntimeError(f"GET sandboxes/{sandbox} answered {status}")

    def state_of(self, sandbox):
        view = self.sandbox(sandbox)
        return view.get("state") if view else "missing"

    def renderers(self):
        status, view = self.request("GET", "sandboxes")
        items = view.get("value", view) if isinstance(view, dict) else view
        return [s for s in items or [] if (s.get("labels") or {}).get("role") == "renderer"]


# ---------------------------------------------------------------------------------------------
# setup
# ---------------------------------------------------------------------------------------------

def setup(ctx):
    commit = spike.run(["git", "-C", str(REPO), "rev-parse", "--short", "HEAD"]).stdout.strip()
    if "resource_group" not in ctx.state:
        stamp = time.strftime("%Y%m%d%H%M%S", time.gmtime())
        resource_group = f"rg-atli-reports-e2e-{stamp}"
        log(f"Resource group {resource_group} in {ctx.region}")
        spike.run(["az", "group", "create", "--name", resource_group, "--location", ctx.region,
                   "--tags", "purpose=atli-reports-hosted-e2e", f"commit={commit}", "-o", "none"])
        ctx.state.save(resource_group=resource_group, sandbox_group=f"reports-e2e-{stamp}",
                       commit=commit, started_utc=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()))
    aca = ctx.aca()
    if "role_propagation_s" not in ctx.state:
        started = time.monotonic()
        aca("sandboxgroup", "create", "--name", ctx.group, "--location", ctx.region, check=False)
        created_s = round(time.monotonic() - started, 1)
        # The group's create grants the caller the Data Owner role; until it propagates, 403.
        started = time.monotonic()
        while aca("sandbox", "list", "-o", "json", check=False).returncode != 0:
            if time.monotonic() - started > 1200:
                raise RuntimeError("The data-plane role did not propagate within 20 minutes.")
            time.sleep(15)
        ctx.state.save(group_create_s=created_s, role_propagation_s=round(time.monotonic() - started, 1))
        log(f"Data-plane role works after {ctx.state['role_propagation_s']} s")

    def build(suffix):
        name = f"reports-server-{commit}-{suffix}"
        work = Path(tempfile.mkdtemp(prefix="atli-e2e-context-"))
        try:
            context = spike.build_context(work)
            started = time.monotonic()
            for attempt in range(6):
                result = aca("sandboxgroup", "disk", "create", "--source", str(context), "--name", name,
                             "--wait-timeout", "3000", check=False, timeout=3600)
                if result.returncode == 0:
                    break
                # The content-package upload can still answer 403 shortly after the role works.
                if "403" not in result.stderr or attempt == 5:
                    raise RuntimeError(f"Disk image {name} failed:\n{result.stderr[-2000:]}")
                time.sleep(30)
            return name, round(time.monotonic() - started, 1)
        finally:
            shutil.rmtree(work, ignore_errors=True)

    missing = [suffix for suffix in ["a", "b"] if f"disk_{suffix}" not in ctx.state]
    if missing:
        log(f"Building disk images {missing} from the server Dockerfile at {commit}")
        with concurrent.futures.ThreadPoolExecutor(len(missing)) as pool:
            built = dict(zip(missing, pool.map(build, missing)))
        disks = json.loads(aca("sandboxgroup", "disk", "list", "-o", "json").stdout)
        for suffix, (name, seconds) in built.items():
            disk = next(d for d in disks if d.get("name") == name)
            ctx.state.save(**{f"disk_{suffix}": disk["id"], f"disk_{suffix}_name": name,
                              f"disk_{suffix}_build_s": seconds})
            log(f"Disk {name}: {disk['id']} in {seconds} s")
    ctx.record("setup", {key: ctx.state.get(key) for key in [
        "commit", "resource_group", "sandbox_group", "started_utc", "group_create_s", "role_propagation_s",
        "disk_a_name", "disk_a_build_s", "disk_b_name", "disk_b_build_s"]}
        | {"aca_cli": aca("--version").stdout.strip(), "location": ctx.region})


# ---------------------------------------------------------------------------------------------
# The provisioner, the gateway, and requests through it
# ---------------------------------------------------------------------------------------------

BIN = REPO / "artifacts/bin"
# Never through a proxy from the environment: the gateway is on 127.0.0.1.
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def elapsed(since):
    return round(time.monotonic() - since, 2)


def scrub(line):
    """A provisioner line without port URLs: anyone holding one can reach the port's proxy."""
    return re.sub(r"https://\S+\.adcproxy\.io/?", "<port URL>", line)


class Provisioner:
    """Runs the provisioner, configured by Provisioner__* environment variables, and keeps each
    command's output, with the second each line appeared, in --work/logs. The provisioner never
    prints credentials or verifiers; the allowed address goes only into its environment."""

    def __init__(self, ctx, public_ip):
        self.ctx = ctx
        self.count = 0
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("Provisioner__")}
        self.env.update({
            "Provisioner__Sandboxes__SubscriptionId": ctx.subscription,
            "Provisioner__Sandboxes__ResourceGroup": ctx.resource_group,
            "Provisioner__Sandboxes__SandboxGroup": ctx.group,
            "Provisioner__Sandboxes__Region": ctx.region,
            "Provisioner__Records__Store": "File",
            "Provisioner__Records__Path": str(ctx.records),
            "Provisioner__DiskImageId": ctx.state["disk_a"],
            "Provisioner__Size": "S",
            "Provisioner__AutoSuspendAfter": "00:01:00",
            "Provisioner__AllowedSourceCidrs__0": f"{public_ip}/32",
            "Provisioner__PortActivation": "OnDemand",
        })

    def __call__(self, *args, overrides=None, check=True):
        self.count += 1
        started = time.monotonic()
        process = subprocess.Popen(
            ["dotnet", str(BIN / "Atli.Reports.Provisioner/release/atli-reports-provisioner.dll"), *args],
            env=dict(self.env, **(overrides or {})), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        lines = [(elapsed(started), line.rstrip("\n")) for line in process.stdout]
        code = process.wait()
        seconds = elapsed(started)
        path = self.ctx.work / "logs" / f"provisioner-{self.count:02d}-{args[0]}.log"
        path.write_text(f"$ provisioner {' '.join(args)}\n"
                        + "".join(f"[{at:7.2f}] {line}\n" for at, line in lines)
                        + f"exit {code} after {seconds} s\n")
        log(f"provisioner {' '.join(args)}: exit {code} in {seconds} s")
        if check and code != 0:
            raise RuntimeError(f"provisioner {' '.join(args)} exited {code}:\n" + "\n".join(l for _, l in lines[-20:]))
        return {"exit": code, "seconds": seconds, "lines": lines}


def parse_list(lines):
    """The record table of `list`: one dict per tenant, and the lines after it."""
    text = [line for _, line in lines]
    rows = {}
    for line in text:
        parts = line.split()
        if len(parts) >= 6 and parts[0] in TENANTS:
            rows[parts[0]] = {"sandbox": parts[1], "state": parts[2], "size": parts[3], "disk": parts[4]}
    return rows, text


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Gateway:
    """The server in gateway mode on 127.0.0.1, its console log in --work/logs/gateway.log."""

    def __init__(self, ctx, caller_server_env):
        self.ctx = ctx
        self.url = f"http://127.0.0.1:{free_port()}"
        env = {k: v for k, v in os.environ.items()
               if not k.startswith(("ReportsServer__", "ReportsEngine__", "ASPNETCORE_", "Kestrel__", "Logging__"))}
        env.update(caller_server_env)
        env.update({
            "ASPNETCORE_URLS": self.url,
            "ReportsServer__Mode": "Gateway",
            "ReportsServer__Gateway__Tenants__0__CallerId": CALLER,
            "ReportsServer__Gateway__Tenants__0__Tenants__0": "acme",
            "ReportsServer__Gateway__Tenants__0__Tenants__1": "globex",
            "ReportsServer__Gateway__Records__Store": "File",
            "ReportsServer__Gateway__Records__Path": str(ctx.records),
            "ReportsServer__Gateway__Wake__Mode": "Sandboxes",
            "ReportsServer__Gateway__Wake__Sandboxes__SubscriptionId": ctx.subscription,
            "ReportsServer__Gateway__Wake__Sandboxes__ResourceGroup": ctx.resource_group,
            "ReportsServer__Gateway__Wake__Sandboxes__SandboxGroup": ctx.group,
            "ReportsServer__Gateway__Wake__Sandboxes__Region": ctx.region,
            "Logging__Console__FormatterName": "simple",
            "Logging__Console__FormatterOptions__SingleLine": "true",
            "Logging__Console__FormatterOptions__UseUtcTimestamp": "true",
            "Logging__Console__FormatterOptions__TimestampFormat": "HH:mm:ss.fff ",
            # "Request finished ... in N ms": the gateway's own duration of each request.
            "Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics": "Information",
        })
        self.log_path = ctx.work / "logs/gateway.log"
        self.log_file = self.log_path.open("w")
        directory = BIN / "Atli.Reports.Server/release"
        self.process = subprocess.Popen(["dotnet", str(directory / "Atli.Reports.Server.dll")], cwd=directory,
                                        env=env, stdout=self.log_file, stderr=subprocess.STDOUT)
        started = time.monotonic()
        while self.health() != 200:
            if self.process.poll() is not None or time.monotonic() - started > 60:
                raise RuntimeError(f"The gateway did not become ready; see {self.log_path}")
            time.sleep(0.25)
        self.ready_s = elapsed(started)

    def health(self):
        try:
            with OPENER.open(f"{self.url}/health/ready", timeout=5) as response:
                return response.status
        except urllib.error.HTTPError as error:
            return error.code
        except OSError:
            return 0

    def log_lines(self):
        self.log_file.flush()
        return self.log_path.read_text().splitlines()

    def stop(self):
        if self.process.poll() is None:
            self.process.send_signal(signal.SIGTERM)
            try:
                self.process.wait(30)
            except subprocess.TimeoutExpired:
                self.process.kill()
        self.log_file.close()


class Caller:
    """Converts through the gateway as the caller, timing each request at the client on the same
    machine (the gateway's own durations are in its log)."""

    def __init__(self, gateway, key, bodies):
        self.gateway = gateway
        self.key = key
        self.bodies = bodies
        self.last = {}

    def convert(self, tenant, fixture="invoice", key=None, header=True):
        headers = {"Content-Type": "application/json", "X-Reports-Api-Key": key or self.key}
        if header:
            headers["X-Reports-Tenant"] = tenant
        request = urllib.request.Request(f"{self.gateway.url}/convert", data=self.bodies[fixture],
                                         headers=headers, method="POST")
        started = time.monotonic()
        try:
            with OPENER.open(request, timeout=180) as response:
                status, data = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, data = error.code, error.read()
        except (OSError, http.client.HTTPException) as error:
            status, data = f"error {type(error).__name__}", b""
        result = {"status": status, "s": elapsed(started), "bytes": len(data)}
        self.last[tenant] = time.monotonic()
        if status == 200:
            result["pdf"] = data.startswith(b"%PDF-")
        else:
            try:
                problem = json.loads(data)
                result["kind"] = problem.get("kind")
                result["detail"] = (problem.get("detail") or problem.get("title") or "")[:160]
            except ValueError:
                result["body"] = data[:160].decode(errors="replace")
        return result


def direct(url, method="GET", data=None, headers=None):
    """A request straight to a renderer's port URL from this machine (an allowed source)."""
    request = urllib.request.Request(url, data=data, headers=headers or {}, method=method)
    started = time.monotonic()
    try:
        with urllib.request.urlopen(request, timeout=60, context=ssl.create_default_context()) as response:
            status, body = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, body = error.code, error.read()
    return {"status": status, "s": elapsed(started), "body": body[:200].decode(errors="replace")}


def read_records(ctx):
    """The tenants' records as the gateway reads them, without their credentials."""
    records = {}
    for path in sorted(ctx.records.glob("*.json")):
        record = json.loads(path.read_text())
        records[record["tenantId"]] = {"url": record["url"], "sandbox": record.get("sandboxId"),
                                       "disk": record.get("diskImageId"),
                                       "max_requests": record.get("maxConcurrentRequests")}
    return records


def record_keys(ctx):
    """Every renderer credential in the store, only to check that no log holds one."""
    return {json.loads(path.read_text())["apiKey"] for path in ctx.records.glob("*.json")}


class Loop(threading.Thread):
    """Converts for one tenant every `interval` seconds, one request at a time."""

    def __init__(self, caller, tenant, interval=0.5):
        super().__init__(daemon=True)
        self.caller, self.tenant, self.interval = caller, tenant, interval
        self.started = time.monotonic()
        self.results = []
        self.halt = threading.Event()

    def run(self):
        while not self.halt.is_set():
            sent = time.monotonic()
            result = self.caller.convert(self.tenant)
            result["t"] = round(sent - self.started, 2)
            self.results.append(result)
            self.halt.wait(max(0.0, self.interval - (time.monotonic() - sent)))

    def stop(self):
        self.halt.set()
        self.join(240)
        return self.results


def wait_stopped(ctx, rest, caller, tenants, limit=600):
    """Polls until each tenant's sandbox is Stopped; seconds after that tenant's last request."""
    records = read_records(ctx)
    pending = {tenant: records[tenant]["sandbox"] for tenant in tenants}
    observed = {}
    started = time.monotonic()
    while pending:
        if time.monotonic() - started > limit:
            raise RuntimeError(f"Not Stopped within {limit} s: {sorted(pending)}")
        for tenant, sandbox in list(pending.items()):
            view = rest.sandbox(sandbox) or {}
            if view.get("state") == "Stopped":
                observed[tenant] = {"after_last_request_s": round(time.monotonic() - caller.last[tenant], 1),
                                    "stopped_reason": (view.get("stateDetails") or view).get("stoppedReason")}
                del pending[tenant]
        time.sleep(2)
    return observed


def gateway_events(lines, tenant, events):
    """Gateway log lines of the given event IDs about the tenant (no secrets: the gateway logs
    tenant and sandbox IDs only)."""
    pattern = re.compile(r"\[(" + "|".join(str(e) for e in events) + r")\]")
    return [line for line in lines if pattern.search(line) and f"tenant {tenant}" in line]


def run(ctx):
    results = {}
    ctx.record("run", results)
    log("Building the provisioner and the server")
    for project in ["Atli.Reports.Provisioner", "Atli.Reports.Server"]:
        spike.run(["dotnet", "build", str(REPO / "src" / project), "-c", "Release", "-nologo", "-v", "q"])

    # The caller's key, as scripts/create-reports-api-key.sh writes it; convert permission only.
    credentials = ctx.work / "caller"
    if not credentials.exists():
        spike.run(["bash", str(REPO / "scripts/create-reports-api-key.sh"), str(credentials), CALLER])
    caller_env = dict(line.split("=", 1) for line in (credentials / "server.env").read_text().split()
                      if "Permissions__1" not in line)
    requests = ctx.work / "requests"
    requests.mkdir(mode=0o700, exist_ok=True)
    spike.write_requests(requests, credentials)
    key = (requests / "api-key").read_text()
    wrong_key = "reports-" + os.urandom(6).hex() + "." + os.urandom(32).hex()
    bodies = {name: (requests / f"{name}.json").read_bytes() for name in ["invoice", "long-table"]}
    # The allowed source address; never printed or written outside --work.
    public_ip = spike.run(["curl", "-fsS", "-4", "https://api.ipify.org"]).stdout.strip()
    if not re.fullmatch(r"\d{1,3}(\.\d{1,3}){3}", public_ip):
        raise RuntimeError("Could not read this machine's public IPv4 address.")
    secrets = {"caller key": key, "wrong key": wrong_key, "public address": public_ip,
               "subscription": ctx.subscription}
    renderer_keys = set()

    ctx.records.mkdir(mode=0o700, exist_ok=True)
    provisioner = Provisioner(ctx, public_ip)
    aca = ctx.aca()
    rest = ctx.rest()
    rest_at = time.monotonic()
    gateway = None
    client = None

    def fresh_rest():
        nonlocal rest, rest_at
        if time.monotonic() - rest_at > 1800:
            rest, rest_at = ctx.rest(), time.monotonic()
        return rest

    try:
        # 2. Tenants.
        log("Creating tenants")
        created = {"acme": provisioner("create", "--tenant", "acme"),
                   "globex": provisioner("create", "--tenant", "globex",
                                         overrides={"Provisioner__PortActivation": "Manual"})}
        listed = provisioner("list")
        rows, _ = parse_list(listed["lines"])
        records = read_records(ctx)
        renderer_keys |= record_keys(ctx)
        ports = {tenant: next((p for p in (fresh_rest().sandbox(records[tenant]["sandbox"]) or {}).get("ports", [])
                               if p.get("port") == 8080), {}) for tenant in TENANTS}
        results["tenants"] = {
            tenant: {"create_s": created[tenant]["seconds"], "sandbox": records[tenant]["sandbox"],
                     "max_concurrent_requests": records[tenant]["max_requests"], "list": rows.get(tenant),
                     "port": {"activationMode": ports[tenant].get("activationMode"),
                              "anonymous": (ports[tenant].get("auth") or {}).get("anonymous"),
                              "ip_default_action": (ports[tenant].get("ipAccessControl") or {}).get("defaultAction"),
                              "ip_rules": len((ports[tenant].get("ipAccessControl") or {}).get("rules") or [])},
                     "create_lines": [scrub(line) for _, line in created[tenant]["lines"] if "URL:" not in line]}
            for tenant in TENANTS}
        ctx.record("run", results)

        # 3. Gateway.
        log("Starting the gateway")
        gateway = Gateway(ctx, caller_env)
        caller = Caller(gateway, key, bodies)
        results["gateway_ready_s"] = gateway.ready_s

        # a. Conversions and refusals.
        log("a. Conversions through the gateway")
        basic = {}
        for tenant in TENANTS:
            basic[f"{tenant}_invoice"] = [caller.convert(tenant) for _ in range(3)]
            basic[f"{tenant}_long_table"] = caller.convert(tenant, "long-table")
        basic["missing_header"] = caller.convert("acme", header=False)
        basic["other_tenant"] = caller.convert("initech")
        basic["wrong_key"] = caller.convert("acme", key=wrong_key)
        basic["no_key"] = caller.convert("acme", key=" ")
        # Straight to acme's renderer from this (allowed) address: the renderer's own key check.
        url = records["acme"]["url"].rstrip("/")
        basic["renderer_direct_no_key"] = direct(f"{url}/convert", "POST", bodies["invoice"],
                                                 {"Content-Type": "application/json"})
        basic["renderer_direct_caller_key"] = direct(f"{url}/convert", "POST", bodies["invoice"],
                                                     {"Content-Type": "application/json", "X-Reports-Api-Key": key})
        caller.last["acme"] = time.monotonic()
        results["a_basic"] = basic
        ctx.record("run", results)

        # The client sandbox for d, created while the renderers idle toward auto-suspend.
        log("Client sandbox in the region")
        client, client_s = aca.create("--disk", "ubuntu", "--cpu", "500m", "--memory", "1024Mi",
                                      "--egress-default", "Deny", "--egress-rule", "*.adcproxy.io:Allow",
                                      "--label", "role=e2e-client")
        results["client_sandbox_create_s"] = round(client_s, 1)

        # b and c. Wakes after auto-suspend.
        log("b/c. Waiting for auto-suspend")
        results["auto_suspend"] = wait_stopped(ctx, fresh_rest(), caller, TENANTS)
        ctx.record("run", results)
        log("b. acme: OnDemand wake through the gateway")
        wake = {"invoice": caller.convert("acme"),
                "state_after": fresh_rest().state_of(records["acme"]["sandbox"])}
        wake["long_table_right_after"] = caller.convert("acme", "long-table")
        wake["invoice_warm"] = caller.convert("acme")
        results["b_ondemand_wake"] = wake
        log("c. globex: the gateway's resume (Manual port)")
        globex_url = records["globex"]["url"].rstrip("/")
        manual = {"direct_while_stopped": direct(f"{globex_url}/health/live"),
                  "state_after_direct": fresh_rest().state_of(records["globex"]["sandbox"])}
        manual["invoice"] = caller.convert("globex")
        manual["invoice_warm"] = caller.convert("globex")
        time.sleep(1)
        manual["gateway_log"] = [line for line in gateway_events(gateway.log_lines(), "globex",
                                                                                  [60, 61, 62, 63])]
        results["c_manual_wake"] = manual
        ctx.record("run", results)

        # d. The port's source allow-list, while acme is stopped; then the report as the waking request.
        log("d. Waiting for acme to stop again")
        results["auto_suspend_2"] = wait_stopped(ctx, fresh_rest(), caller, ["acme"])
        log("d. A request from another address")
        probe = aca.exec(client, (
            f"curl -s -o /tmp/body -w '%{{http_code}} %{{time_total}}' -m 60 -X POST "
            f"-H 'Content-Type: application/json' --data '{{\"html\":\"<p>x</p>\"}}' {url}/convert; "
            f"echo; head -c 200 /tmp/body; echo; "
            f"curl -s -o /tmp/body -w '%{{http_code}}' -m 60 {url}/health/live; echo; head -c 200 /tmp/body"),
            check=False).stdout.splitlines()
        time.sleep(10)
        results["d_ip_allow_list"] = {"client_lines": probe,
                                      "acme_state_10s_later": fresh_rest().state_of(records["acme"]["sandbox"])}
        log("b. acme: the 49-page report as the waking request")
        results["b_ondemand_wake"]["long_table_as_waking_request"] = caller.convert("acme", "long-table")
        ctx.record("run", results)

        # e. Rollout under load.
        log("e. Rollout to disk b while converting for acme every 0.5 s")
        before = read_records(ctx)
        loop = Loop(caller, "acme")
        loop.start()
        time.sleep(5)
        rollout_started = round(time.monotonic() - loop.started, 2)
        rollout = provisioner("rollout", "--disk-image", ctx.state["disk_b"], "--drain", "00:00:40", check=False)
        rollout_ended = round(time.monotonic() - loop.started, 2)
        time.sleep(35)
        statuses = loop.stop()
        after = read_records(ctx)
        renderer_keys |= record_keys(ctx)
        prune = provisioner("prune", "--drain", "00:00:00", check=False)
        listed = provisioner("list")
        counts = {}
        for result in statuses:
            counts[str(result["status"])] = counts.get(str(result["status"]), 0) + 1
        ok = [r["s"] for r in statuses if r["status"] == 200]
        results["e_rollout"] = {
            "exit": rollout["exit"], "seconds": rollout["seconds"],
            "loop_rollout_started_at_s": rollout_started, "loop_rollout_ended_at_s": rollout_ended,
            "rollout_lines": [f"[{at:7.2f}] {scrub(line)}" for at, line in rollout["lines"] if "URL:" not in line],
            "requests": len(statuses), "statuses": counts,
            "non_200": [r for r in statuses if r["status"] != 200],
            "latency_s": {"min": min(ok), "median": sorted(ok)[len(ok) // 2], "max": max(ok)} if ok else None,
            "slowest": sorted(statuses, key=lambda r: -r["s"])[:5],
            "records": {tenant: {"before": before[tenant]["sandbox"], "after": after[tenant]["sandbox"],
                                 "disk_after_is_b": after[tenant]["disk"] == ctx.state["disk_b"],
                                 "old_sandbox_state": fresh_rest().state_of(before[tenant]["sandbox"]),
                                 "new_sandbox_state": fresh_rest().state_of(after[tenant]["sandbox"])}
                        for tenant in TENANTS},
            "prune": {"exit": prune["exit"], "lines": [line for _, line in prune["lines"]]},
            "list": parse_list(listed["lines"])[1],
        }
        ctx.record("run", results)
        records = after
        url = records["acme"]["url"].rstrip("/")

        # f. Kill switch.
        log("f. disable acme")
        warm = caller.convert("acme")
        disable = provisioner("disable", "--tenant", "acme")
        disabled_at = time.monotonic()
        attempts = []
        while time.monotonic() - disabled_at < 120:
            result = caller.convert("acme")
            result["t"] = elapsed(disabled_at)
            attempts.append(result)
            if len([a for a in attempts if a["status"] != 200]) >= 3:
                break
            time.sleep(1)
        view = fresh_rest().sandbox(records["acme"]["sandbox"]) or {}
        state_while_disabled = {"state": view.get("state"),
                                "stopped_reason": (view.get("stateDetails") or view).get("stoppedReason")}
        direct_while_disabled = direct(f"{url}/health/live")
        log("f. enable acme")
        enable = provisioner("enable", "--tenant", "acme")
        enabled_at = time.monotonic()
        after_enable = []
        while time.monotonic() - enabled_at < 120:
            result = caller.convert("acme")
            result["t"] = elapsed(enabled_at)
            after_enable.append(result)
            if result["status"] == 200:
                break
            time.sleep(1)
        after_enable.append(caller.convert("acme"))
        results["f_kill_switch"] = {
            "warm_before": warm, "disable_s": disable["seconds"], "during_disable": attempts,
            "sandbox_while_disabled": state_while_disabled, "direct_while_disabled": direct_while_disabled,
            "enable_s": enable["seconds"], "after_enable": after_enable,
            "gateway_log": [line for line in gateway_events(gateway.log_lines(), "acme",
                                                                             [43, 45, 50, 51, 54, 55, 59, 60, 61, 62, 63])][-12:],
        }
        ctx.record("run", results)

        # g. Delete globex while the gateway holds its record.
        log("g. delete globex")
        cached = caller.convert("globex")
        delete = provisioner("delete", "--tenant", "globex")
        deleted_at = time.monotonic()
        attempts = []
        while time.monotonic() - deleted_at < 45:
            result = caller.convert("globex")
            result["t"] = elapsed(deleted_at)
            attempts.append(result)
            time.sleep(2)
        listed = provisioner("list")
        results["g_delete"] = {
            "convert_before": cached, "delete_s": delete["seconds"],
            "delete_lines": [line for _, line in delete["lines"]], "after_delete": attempts,
            "old_sandbox_state": fresh_rest().state_of(records["globex"]["sandbox"]),
            "list": parse_list(listed["lines"])[1],
            "acme_still_converts": caller.convert("acme"),
            "gateway_log": [line for line in gateway_events(gateway.log_lines(), "globex",
                                                            [40, 43, 45, 50, 51, 55, 57, 60, 61, 62, 63])][-10:],
        }
        ctx.record("run", results)

        # h. A port that does not admit the gateway's address: acme's port re-added (as the
        # provisioner adds it) with only a documentation range allowed.
        log("h. acme's port refusing the gateway's address")
        sandbox = records["acme"]["sandbox"]
        port = {"port": 8080, "auth": {"anonymous": True}, "activationMode": "OnDemand",
                "ipAccessControl": {"defaultAction": "Deny", "rules": [
                    {"name": "elsewhere", "action": "Allow", "priority": 100, "sourceCidrs": ["203.0.113.0/24"]}]}}
        fresh_rest().request("POST", f"sandboxes/{sandbox}/ports/remove", {"port": 8080})
        status, _ = fresh_rest().request("POST", f"sandboxes/{sandbox}/ports/add", port)
        time.sleep(5)
        refused = [caller.convert("acme") for _ in range(2)]
        results["h_address_refused"] = {
            "port_add_status": status, "converts": refused,
            "acme_state": fresh_rest().state_of(sandbox),
            "gateway_log": [line for line in gateway_events(gateway.log_lines(), "acme", [45, 58])][-4:],
        }
        ctx.record("run", results)
    finally:
        if gateway:
            gateway.stop()
        if client:
            aca("sandbox", "delete", "--id", client, "--yes", check=False)
        # 5. The provisioner deletes what is left; cleanup deletes the resource group.
        if ctx.records.exists():
            renderer_keys |= record_keys(ctx)
            for tenant in sorted(read_records(ctx)):
                provisioner("delete", "--tenant", tenant, check=False)
            final = provisioner("list", check=False)
            results["final_list"] = [line for _, line in final["lines"]]
            results["renderer_sandboxes_left"] = len(fresh_rest().renderers())
        # No log, and not the results, may hold a credential, this machine's address, or the
        # subscription ID.
        found = {}
        files = sorted((ctx.work / "logs").glob("*")) + ([ctx.output] if ctx.output else [])
        for path in files:
            text = path.read_text(errors="replace") if path.exists() else ""
            for name, value in list(secrets.items()) + [(f"renderer key {i}", k) for i, k in enumerate(renderer_keys)]:
                if value and value in text:
                    found.setdefault(path.name, []).append(name)
        results["secrets_in_logs_and_results"] = found or "none"
        results["renderer_keys_checked"] = len(renderer_keys)
        results["verdicts"] = verdicts(results)
        ctx.record("run", results)
        for check, verdict in results["verdicts"].items():
            log(f"{check}: {verdict}")


def verdicts(r):
    """Pass or fail per check, from the recorded results."""
    def ok(result):
        return result.get("status") == 200 and result.get("pdf") is True

    def check(name, condition):
        try:
            return "pass" if condition() else "FAIL"
        except (KeyError, IndexError, TypeError, ValueError):
            return "not run"

    def first_status(line_list):
        return line_list[0].split()[0]

    return {
        "a_convert_and_refusals": check("a", lambda: all(ok(x) for t in TENANTS for x in r["a_basic"][f"{t}_invoice"])
                                        and all(ok(r["a_basic"][f"{t}_long_table"]) for t in TENANTS)
                                        and r["a_basic"]["missing_header"]["status"] == 400
                                        and r["a_basic"]["other_tenant"]["status"] == 403
                                        and r["a_basic"]["wrong_key"]["status"] == 401
                                        and r["a_basic"]["no_key"]["status"] == 401
                                        and r["a_basic"]["renderer_direct_no_key"]["status"] == 401
                                        and r["a_basic"]["renderer_direct_caller_key"]["status"] == 401),
        "b_ondemand_wake": check("b", lambda: "acme" in r["auto_suspend"] and ok(r["b_ondemand_wake"]["invoice"])
                                 and ok(r["b_ondemand_wake"]["long_table_right_after"])
                                 and ok(r["b_ondemand_wake"]["long_table_as_waking_request"])),
        "c_manual_wake": check("c", lambda: "globex" in r["auto_suspend"]
                               and r["c_manual_wake"]["direct_while_stopped"]["status"] == 403
                               and r["c_manual_wake"]["state_after_direct"] == "Stopped"
                               and ok(r["c_manual_wake"]["invoice"])
                               and any("[60]" in line for line in r["c_manual_wake"]["gateway_log"])
                               and any("[61]" in line for line in r["c_manual_wake"]["gateway_log"])),
        "d_ip_allow_list": check("d", lambda: first_status(r["d_ip_allow_list"]["client_lines"]) == "403"
                                 and "IpAccessDenied" in r["d_ip_allow_list"]["client_lines"][1]
                                 and r["d_ip_allow_list"]["acme_state_10s_later"] == "Stopped"),
        "e_rollout": check("e", lambda: r["e_rollout"]["exit"] == 0 and r["e_rollout"]["requests"] > 0
                           and not r["e_rollout"]["non_200"]
                           and all(v["disk_after_is_b"] and v["old_sandbox_state"] == "missing"
                                   and v["before"] != v["after"] for v in r["e_rollout"]["records"].values())
                           and "No renderer sandboxes are left over." in r["e_rollout"]["prune"]["lines"]),
        "f_kill_switch": check("f", lambda: r["f_kill_switch"]["during_disable"][0]["status"] == 503
                               and all(a["status"] == 503 and a["s"] < 5 for a in r["f_kill_switch"]["during_disable"])
                               and ok(r["f_kill_switch"]["after_enable"][-2])
                               and ok(r["f_kill_switch"]["after_enable"][-1])),
        "g_delete": check("g", lambda: all(a["status"] == 503 for a in r["g_delete"]["after_delete"])
                          and r["g_delete"]["after_delete"][-1]["detail"] == "The tenant has no renderer."
                          and not any(line.split()[0] == "globex" for line in r["g_delete"]["list"] if line)
                          and ok(r["g_delete"]["acme_still_converts"])),
        "h_address_refused": check("h", lambda: all(c["status"] == 503 for c in r["h_address_refused"]["converts"])
                                   and any("[58]" in line for line in r["h_address_refused"]["gateway_log"])),
        "cleanup_by_provisioner": check("cleanup", lambda: r["final_list"] == ["No renderers."]
                                        and r["renderer_sandboxes_left"] == 0),
        "no_secrets_in_logs": check("secrets", lambda: r["secrets_in_logs_and_results"] == "none"),
    }


# ---------------------------------------------------------------------------------------------
# cleanup
# ---------------------------------------------------------------------------------------------

def cleanup(ctx):
    if "resource_group" not in ctx.state:
        log("Nothing to clean up.")
        return
    results = {}
    aca = ctx.aca()
    started = time.monotonic()
    if ctx.state.get("role_propagation_s") is not None:
        log(f"Deleting sandbox group {ctx.group}")
        deleted = aca("sandboxgroup", "delete", "--name", ctx.group, "--yes", check=False, timeout=3600)
        results["sandbox_group_delete"] = {"exit": deleted.returncode,
                                           "seconds": round(time.monotonic() - started, 1)}
    log(f"Deleting {ctx.resource_group}")
    started = time.monotonic()
    spike.run(["az", "group", "delete", "--name", ctx.resource_group, "--yes", "--no-wait"], check=False)
    while spike.run(["az", "group", "exists", "--name", ctx.resource_group], check=False).stdout.strip() != "false":
        if time.monotonic() - started > 3600:
            raise RuntimeError(f"{ctx.resource_group} still exists after an hour")
        time.sleep(20)
    results["resource_group_deleted_after_s"] = round(time.monotonic() - started, 1)
    results["resource_group_exists"] = False
    results["finished_utc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    ctx.record("cleanup", results)
    log(f"{ctx.resource_group} is gone")


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("command", choices=["setup", "run", "cleanup", "all"])
    parser.add_argument("--work", type=Path, help="state, secrets, and logs (mode 0700); a new temp dir by default")
    parser.add_argument("--output", type=Path, help="results JSON (no secrets)")
    parser.add_argument("--location", default="eastus2")
    parser.add_argument("--aca", type=Path, help="the aca binary, when it is not on PATH")
    parser.add_argument("--keep-work", action="store_true", help="cleanup: keep --work")
    options = parser.parse_args()
    if options.aca:
        os.environ["PATH"] = f"{options.aca.resolve().parent}{os.pathsep}{os.environ['PATH']}"
    for tool in ["az", "aca", "dotnet", "git", "openssl", "curl"]:
        if not shutil.which(tool):
            sys.exit(f"Missing required tool: {tool}")
    if options.work is None:
        if options.command != "all":
            sys.exit(f"{options.command} needs --work")
        options.work = Path(tempfile.mkdtemp(prefix="atli-e2e-"))
    if options.output is None and options.command == "all":
        # Outside --work, which cleanup removes.
        options.output = Path(tempfile.gettempdir()) / f"atli-e2e-results-{time.strftime('%Y%m%d%H%M%S')}.json"
    ctx = Context(options)
    log(f"Work directory {ctx.work}; results in {ctx.output}")
    if options.command == "all":
        try:
            setup(ctx)
            run(ctx)
        finally:
            cleanup(ctx)
            if not options.keep_work:
                shutil.rmtree(ctx.work, ignore_errors=True)
    elif options.command == "cleanup":
        cleanup(ctx)
        if not options.keep_work:
            shutil.rmtree(ctx.work, ignore_errors=True)
    else:
        globals()[options.command](ctx)


if __name__ == "__main__":
    main()
