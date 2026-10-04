#!/usr/bin/env python3
"""A production-shaped run of the hosted renderer service on Azure Container Apps Sandboxes.

Everything runs in resource groups of its own, rg-atli-reports-prod-shape-<UTC stamp> (and the
Container Apps environment's infrastructure group, <that>-cae), tagged
purpose=atli-reports-production-shape, plus one custom role definition whose only assignable scope
is that group. `cleanup` deletes all of it and checks.

  setup     The renderer network (a virtual network whose DNS server is an unused address of its own
            range, a subnet delegated to Microsoft.App/environments, and a network security group
            denying outbound traffic to the AzurePlatformDNS service tag); the gateway's network (a
            second virtual network, a /27 subnet, a NAT gateway with a static Standard public
            address); three sandbox groups: renderers connected to the renderer network
            (`aca sandboxgroup network create --name renderers`), control renderers without a
            network, and the load client; disk images built remotely from this commit's server
            Dockerfile (a, and b as the rollout target, for the connected group; a for the control
            group); a Key Vault (RBAC, no purge protection) as the record store; a Basic container
            registry with the server image built by `az acr build`; a user-assigned identity for the
            gateway with AcrPull, Key Vault Secrets User, and a custom resume-only role on the
            connected group; a Log Analytics workspace; a workload-profiles Container Apps
            environment on the gateway subnet; the gateway as a Container App (external HTTPS
            ingress to 8080, liveness and readiness probes, API-key callers, the Key Vault store and
            Wake:Mode=Sandboxes through the identity); and the client sandbox (python-3.12 disk,
            egress allowed only to the gateway's host and the port proxy).
  tenants   The provisioner (Release build, Records:Store=KeyVault, the ports admitting the NAT
            gateway's /32 and this machine's /32) creates the tenants: vs1, vs2 (S), vm1, vm2 (M),
            vl1, vl2 (L), vman (M, Manual port), and vauto (S, auto-suspend after 60 s) in the
            connected group; cm1 (M) in the control group, whose port also admits the client's
            outbound addresses.
  hardening Inside vm1 and cm1: resolv.conf, dns-probe.pl, network-probe.sh, and
            chromium-sandbox-check.sh; conversions through the gateway; the port through the proxy;
            the client's request refused by address (403 IpAccessDenied) without waking vm1.
  creates   Interleaved creates of size-M renderers in the connected and the control group over the
            data plane, as the provisioner sends them: the create call, the port call, and the time
            to /health/ready from this machine; each deleted afterwards.
  wake      Through the gateway, from the client: vm1 and cm1 stopped (data-plane stop, memory
            mode) and woken by an invoice or the 49-page report (OnDemand ports); vauto woken after
            auto-suspend; then, after a gateway restart, vman (Manual port) woken by the gateway's
            resume through its identity, with events 60 and 61 from the gateway's log.
  warm      Sequential warm conversions through the gateway (S, M, and L in the connected group),
            and cm1 through the gateway and straight to its port, interleaved.
  load      Sustained closed-loop load through the gateway on the six load tenants
            (--duration, --factor times each tenant's admitted concurrency, --replicas gateway
            replicas), with the renderers' VM memory, CPU, and OOM kills sampled, the gateway's CPU
            and memory from Container Apps metrics, and renderer 429s from the gateway's log.
  rollout   Deletes cm1, vman, and vauto, then load at admitted concurrency with a rollout of the
            connected group to disk b a minute in.
  verify    Searches the logs and the results for every secret, address, and ID of the run.
  cleanup   Deletes the provisioner's tenants, the role assignments and the role definition, the
            workspace (permanently), the sandbox groups and their network connection, and the
            resource groups; purges the vault; checks that each is gone.

Secrets (the caller's key, renderer credentials, tokens, the workspace key) and addresses (this
machine's, the NAT gateway's, the client's outbound ones) stay in --work (mode 0700), in files of
mode 0600; nothing prints them, the subscription, tenant, or object IDs. Logs are redacted as
they are written, and `verify` searches them and the results again.

Requirements: az (logged in, with rights to create resource groups, role definitions, and role
assignments), aca (https://aka.ms/aca-cli-install; --aca), the .NET SDK, git, openssl, and curl.

  benchmarks/azure-sandboxes/production.py setup|tenants|hardening|creates|wake|warm|load|rollout|verify|cleanup \
      --work DIR --output results.json [--location eastus2] [--aca PATH]
"""
import argparse
import base64
import concurrent.futures
import hashlib
import http.client
import json
import os
import re
import secrets as random_secrets
import shutil
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
import run as spike  # noqa: E402  run.py's helpers: write_requests, build_context, ENTRYPOINT
import followup  # noqa: E402  followup.py's SAMPLER

REPO = spike.REPO
API_VERSION = spike.API_VERSION
PURPOSE = "atli-reports-production-shape"
PREFIX = "rg-atli-reports-prod-shape-"
CALLER = "prod-shape-app"
APP = "ca-gateway"
ARM_APP_API = "2024-03-01"
# Tenant: (group, size, provisioner overrides). The control tenant's port also admits the client.
TENANTS = {
    "vs1": ("vnet", "S", {}), "vs2": ("vnet", "S", {}),
    "vm1": ("vnet", "M", {}), "vm2": ("vnet", "M", {}),
    "vl1": ("vnet", "L", {}), "vl2": ("vnet", "L", {}),
    "vman": ("vnet", "M", {"Provisioner__PortActivation": "Manual"}),
    "vauto": ("vnet", "S", {"Provisioner__AutoSuspendAfter": "00:01:00"}),
    "cm1": ("control", "M", {}),
}
LOAD_TENANTS = ["vs1", "vs2", "vm1", "vm2", "vl1", "vl2"]
ADMITTED = {"S": 2, "M": 2, "L": 4}
SIZES = {"S": ("500m", "1024Mi", 1), "M": ("1000m", "2048Mi", 1), "L": ("2000m", "4096Mi", 2)}
BIN = REPO / "artifacts/bin"


# ---------------------------------------------------------------------------------------------
# Redaction, logging, and commands
# ---------------------------------------------------------------------------------------------

SENSITIVE = {}  # placeholder -> value; every log line, error, and result is redacted against it
_lock = threading.Lock()


def sensitive(name, value):
    if value and len(str(value)) >= 6:
        with _lock:
            SENSITIVE[f"<{name}>"] = str(value)


def redact(text):
    text = str(text)
    with _lock:
        items = sorted(SENSITIVE.items(), key=lambda item: -len(item[1]))
    for name, value in items:
        text = text.replace(value, name)
    # Tokens and keys that were never registered: bearer tokens, JWTs, renderer credentials.
    text = re.sub(r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", "<token>", text)
    text = re.sub(r"reports-[0-9a-f]{12}\.[0-9a-f]{64}", "<api key>", text)
    return text


def log(message):
    print(f"[{time.strftime('%H:%M:%S')}] {redact(message)}", flush=True)


def run(command, check=True, env=None, timeout=1800, input=None):
    result = subprocess.run(command, capture_output=True, text=True, env=env, timeout=timeout,
                            check=False, input=input)
    if check and result.returncode != 0:
        raise RuntimeError(redact(f"{' '.join(command[:4])} ... exited {result.returncode}:\n"
                                  f"{result.stderr[-1500:]}"))
    return result


def az(*args, check=True, timeout=1800):
    return run(["az", *args], check=check, timeout=timeout)


def az_json(*args, check=True, timeout=1800):
    result = az(*args, "-o", "json", check=check, timeout=timeout)
    return json.loads(result.stdout or "null") if result.returncode == 0 else None


def utc():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def elapsed(since):
    return round(time.monotonic() - since, 2)


# ---------------------------------------------------------------------------------------------
# State and context
# ---------------------------------------------------------------------------------------------

class Store(dict):
    """A JSON file of mode 0600 in --work."""

    def __init__(self, path):
        super().__init__(json.loads(path.read_text()) if path.exists() else {})
        self.path = path

    def save(self, **values):
        with _lock:
            self.update(values)
            self.path.write_text(json.dumps(self, indent=2) + "\n")
            self.path.chmod(0o600)


class Context:
    def __init__(self, options):
        self.options = options
        self.work = options.work
        self.work.mkdir(mode=0o700, parents=True, exist_ok=True)
        self.work.chmod(0o700)
        for name in ["logs", "client"]:
            (self.work / name).mkdir(mode=0o700, exist_ok=True)
        # Names and resource IDs; resource IDs hold the subscription, so they never print.
        self.state = Store(self.work / "state.json")
        # Addresses, IDs, and secrets: never printed, never in results.
        self.secret = Store(self.work / "sensitive.json")
        self.output = options.output
        self.results = json.loads(self.output.read_text()) if self.output and self.output.exists() else {}
        self.region = options.location
        if "subscription" not in self.secret:
            account = az_json("account", "show")
            self.secret.save(subscription=account["id"], tenant=account["tenantId"])
        self.subscription = self.secret["subscription"]
        sensitive("subscription", self.subscription)
        sensitive("tenant id", self.secret["tenant"])
        for key, value in self.secret.items():
            if key not in ("subscription", "tenant") and isinstance(value, str):
                sensitive(key, value)
            elif isinstance(value, list):
                for index, item in enumerate(value):
                    sensitive(f"{key} {index}", item)

    def record(self, key, value):
        self.results[key] = value
        if self.output:
            self.output.parent.mkdir(parents=True, exist_ok=True)
            self.output.write_text(redact(json.dumps(self.results, indent=2)) + "\n")
            self.output.chmod(0o600)

    @property
    def rg(self):
        return self.state["resource_group"]

    def rg_id(self):
        return f"/subscriptions/{self.subscription}/resourceGroups/{self.rg}"

    def group(self, key):
        return self.state[f"group_{key}"]

    def group_id(self, key):
        return f"{self.rg_id()}/providers/Microsoft.App/sandboxGroups/{self.group(key)}"

    def aca(self, key):
        return Aca(self.subscription, self.rg, self.group(key), self.region)

    def rest(self, key):
        return Rest(self.subscription, self.rg, self.group(key), self.region)


class Aca:
    def __init__(self, subscription, resource_group, group, region):
        self.env = dict(os.environ, ACA_SUBSCRIPTION=subscription, ACA_RESOURCE_GROUP=resource_group,
                        ACA_SANDBOX_GROUP=group, ACA_REGION=region)

    def __call__(self, *args, check=True, timeout=1800):
        return run(["aca", *args], check=check, env=self.env, timeout=timeout)

    def exec(self, sandbox, command, check=True, timeout=900):
        return self("sandbox", "exec", "--id", sandbox, "-c", command, check=check, timeout=timeout)

    def write(self, sandbox, path, file):
        self("sandbox", "fs", "write", "--id", sandbox, "--path", path, "--file", str(file))


_tokens = {}


def data_plane_token():
    """One token for https://dynamicsessions.io, renewed after 30 minutes; never printed."""
    with _lock:
        cached = _tokens.get("dp")
        if cached and time.monotonic() - cached[1] < 1800:
            return cached[0]
    token = az("account", "get-access-token", "--resource", "https://dynamicsessions.io",
               "--query", "accessToken", "-o", "tsv").stdout.strip()
    with _lock:
        _tokens["dp"] = (token, time.monotonic())
    sensitive("data-plane token", token)
    return token


class Rest:
    """The Sandboxes data plane over HTTPS with a cached token: (status, body) per call."""

    def __init__(self, subscription, resource_group, group, region):
        self.base = (f"https://management.{region}.azuredevcompute.io/subscriptions/{subscription}"
                     f"/resourceGroups/{resource_group}/sandboxGroups/{group}")

    def request(self, method, path, body=None, timeout=300):
        url = f"{self.base}/{path}?api-version={API_VERSION}" if path else f"{self.base}?api-version={API_VERSION}"
        request = urllib.request.Request(url, method=method,
                                         data=None if body is None else json.dumps(body).encode(),
                                         headers={"Authorization": f"Bearer {data_plane_token()}",
                                                  "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                status, data = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, data = error.code, error.read()
        except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
            return 0, {"error": type(error).__name__}
        try:
            return status, json.loads(data or b"{}")
        except ValueError:
            return status, {"text": data[:300].decode(errors="replace")}

    def timed(self, method, path, body=None):
        started = time.monotonic()
        status, view = self.request(method, path, body)
        return status, view, time.monotonic() - started

    def sandbox(self, sandbox):
        for attempt in range(5):
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

    def wait(self, sandbox, state, limit=600, interval=0.5):
        started = time.monotonic()
        while self.state_of(sandbox) != state:
            if time.monotonic() - started > limit:
                raise RuntimeError(f"{sandbox} did not reach {state} within {limit} s")
            time.sleep(interval)
        return time.monotonic() - started

    def stop(self, sandbox):
        started = time.monotonic()
        status, _ = self.request("POST", f"sandboxes/{sandbox}/stop", {})
        call_s = time.monotonic() - started
        if status >= 300:
            raise RuntimeError(f"stop {sandbox}: {status}")
        self.wait(sandbox, "Stopped", interval=0.5)
        return {"stop_call_s": round(call_s, 2), "stopped_after_s": elapsed(started)}

    def ensure_running(self, sandbox):
        if self.state_of(sandbox) != "Running":
            self.request("POST", f"sandboxes/{sandbox}/resume", {})
            self.wait(sandbox, "Running")


def http_get(url, timeout=10):
    """A GET from this machine: (status, seconds, body)."""
    started = time.monotonic()
    try:
        with urllib.request.urlopen(urllib.request.Request(url), timeout=timeout,
                                    context=ssl.create_default_context()) as response:
            return response.status, time.monotonic() - started, response.read()[:300]
    except urllib.error.HTTPError as error:
        return error.code, time.monotonic() - started, error.read()[:300]
    except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.HTTPException):
        return 0, time.monotonic() - started, b""


def laptop_ip():
    address = run(["curl", "-fsS", "-4", "https://api.ipify.org"]).stdout.strip()
    if not re.fullmatch(r"\d{1,3}(\.\d{1,3}){3}", address):
        raise RuntimeError("Could not read this machine's public IPv4 address.")
    return address


def check_laptop_ip(ctx):
    address = laptop_ip()
    if ctx.secret.get("laptop_ip") and address != ctx.secret["laptop_ip"]:
        raise RuntimeError("This machine's public address changed since setup; the ports no longer admit it.")
    ctx.secret.save(laptop_ip=address)
    sensitive("laptop ip", address)


# ---------------------------------------------------------------------------------------------
# setup
# ---------------------------------------------------------------------------------------------

def tags():
    return ["--tags", f"purpose={PURPOSE}"]


def setup(ctx):
    commit = run(["git", "-C", str(REPO), "rev-parse", "--short", "HEAD"]).stdout.strip()
    if "resource_group" not in ctx.state:
        stamp = time.strftime("%Y%m%d%H%M%S", time.gmtime())
        rg = PREFIX + stamp
        log(f"Resource group {rg} in {ctx.region}")
        az("group", "create", "--name", rg, "--location", ctx.region, *tags(), f"commit={commit}", "-o", "none")
        ctx.state.save(resource_group=rg, stamp=stamp, commit=commit, started_utc=utc(),
                       infra_rg=f"{rg}-cae",
                       group_vnet=f"reports-ps-vnet-{stamp}", group_control=f"reports-ps-control-{stamp}",
                       group_client=f"reports-ps-client-{stamp}",
                       vault=f"kv-atlips-{stamp}", registry=f"acratlips{stamp}",
                       workspace=f"log-atli-ps-{stamp}", identity=f"id-atli-gateway-{stamp}",
                       environment=f"cae-atli-ps-{stamp}",
                       role_name=f"Atli Reports Sandbox Resumer (prod-shape {stamp})")
    check_laptop_ip(ctx)
    me = az("ad", "signed-in-user", "show", "--query", "id", "-o", "tsv").stdout.strip()
    ctx.secret.save(my_object_id=me)
    sensitive("my object id", me)
    timings = ctx.results.get("setup", {}).get("timings_s", {})

    def timed(name, function):
        started = time.monotonic()
        function()
        timings[name] = elapsed(started)
        log(f"{name}: {timings[name]} s")

    with concurrent.futures.ThreadPoolExecutor(8) as pool:
        networks = pool.submit(timed, "networks", lambda: setup_networks(ctx))
        vault = pool.submit(timed, "vault", lambda: setup_vault(ctx))
        identity = pool.submit(timed, "identity_and_workspace", lambda: setup_identity(ctx))
        image = pool.submit(timed, "registry_and_image", lambda: setup_registry(ctx, commit))
        groups = pool.submit(timed, "sandbox_groups_and_disks", lambda: setup_sandbox_groups(ctx, networks, commit))
        for future in [networks, identity]:
            future.result()
        environment = pool.submit(timed, "container_apps_environment", lambda: setup_environment(ctx))
        for future in [vault, image, groups]:
            future.result()
        timed("role_assignments", lambda: setup_roles(ctx))
        environment.result()
    timed("gateway", lambda: deploy_gateway(ctx, replicas=1))
    timed("client_sandbox", lambda: setup_client(ctx))
    ctx.record("setup", {
        "commit": ctx.state["commit"], "location": ctx.region, "started_utc": ctx.state["started_utc"],
        "resource_group": ctx.rg, "aca_cli": run(["aca", "--version"]).stdout.strip(),
        "az_cli": az("version", "--query", '"azure-cli"', "-o", "tsv").stdout.strip(),
        "timings_s": timings, "disks": {k: ctx.state.get(k) for k in
                                         ["disk_vnet_a_build_s", "disk_vnet_b_build_s", "disk_control_a_build_s"]},
        "role_propagation_s": {k: ctx.state.get(f"propagation_{k}") for k in ["vnet", "control", "client"]},
        "acr_build_s": ctx.state.get("acr_build_s"),
        "gateway": ctx.state.get("gateway_deploys", [])[:1],
        "client": ctx.state.get("client_view"),
    })


def setup_networks(ctx):
    rg = ctx.rg
    if "renderer_subnet_id" not in ctx.state:
        log("Renderer network: DNS server 10.42.0.4 (unused), subnet 10.42.1.0/24, NSG denying AzurePlatformDNS")
        az("network", "nsg", "create", "-g", rg, "-n", "nsg-renderers", "-l", ctx.region, *tags(), "-o", "none")
        az("network", "nsg", "rule", "create", "-g", rg, "--nsg-name", "nsg-renderers", "-n", "deny-azure-platform-dns",
           "--priority", "100", "--direction", "Outbound", "--access", "Deny", "--protocol", "*",
           "--source-address-prefixes", "*", "--source-port-ranges", "*",
           "--destination-address-prefixes", "AzurePlatformDNS", "--destination-port-ranges", "*", "-o", "none")
        az("network", "vnet", "create", "-g", rg, "-n", "vnet-renderers", "-l", ctx.region, *tags(),
           "--address-prefixes", "10.42.0.0/16", "--dns-servers", "10.42.0.4", "-o", "none")
        subnet = az_json("network", "vnet", "subnet", "create", "-g", rg, "--vnet-name", "vnet-renderers",
                         "-n", "renderers", "--address-prefixes", "10.42.1.0/24",
                         "--delegations", "Microsoft.App/environments", "--network-security-group", "nsg-renderers")
        ctx.state.save(renderer_subnet_id=subnet["id"])
    if "gateway_subnet_id" not in ctx.state:
        log("Gateway network: subnet 10.60.0.0/27 with a NAT gateway and a static Standard public address")
        az("network", "public-ip", "create", "-g", rg, "-n", "pip-gateway", "-l", ctx.region, *tags(),
           "--sku", "Standard", "--allocation-method", "Static", "--version", "IPv4", "-o", "none")
        az("network", "nat", "gateway", "create", "-g", rg, "-n", "nat-gateway", "-l", ctx.region, *tags(),
           "--public-ip-addresses", "pip-gateway", "--idle-timeout", "4", "-o", "none")
        az("network", "vnet", "create", "-g", rg, "-n", "vnet-gateway", "-l", ctx.region, *tags(),
           "--address-prefixes", "10.60.0.0/16", "-o", "none")
        subnet = az_json("network", "vnet", "subnet", "create", "-g", rg, "--vnet-name", "vnet-gateway",
                         "-n", "gateway", "--address-prefixes", "10.60.0.0/27",
                         "--delegations", "Microsoft.App/environments", "--nat-gateway", "nat-gateway")
        ctx.state.save(gateway_subnet_id=subnet["id"])
    nat_ip = az("network", "public-ip", "show", "-g", rg, "-n", "pip-gateway", "--query", "ipAddress",
                "-o", "tsv").stdout.strip()
    ctx.secret.save(nat_ip=nat_ip)
    sensitive("nat ip", nat_ip)


def setup_vault(ctx):
    rg, vault = ctx.rg, ctx.state["vault"]
    if "vault_id" not in ctx.state:
        log(f"Key Vault {vault} (RBAC, no purge protection, 7-day retention)")
        view = az_json("keyvault", "create", "-g", rg, "-n", vault, "-l", ctx.region, *tags(),
                       "--enable-rbac-authorization", "true", "--retention-days", "7")
        ctx.state.save(vault_id=view["id"], vault_uri=view["properties"]["vaultUri"])
        if view["properties"].get("enablePurgeProtection"):
            raise RuntimeError("The vault has purge protection on; it could not be purged afterwards.")
    if not ctx.state.get("vault_officer_assigned"):
        az("role", "assignment", "create", "--assignee-object-id", ctx.secret["my_object_id"],
           "--assignee-principal-type", "User", "--role", "Key Vault Secrets Officer",
           "--scope", ctx.state["vault_id"], "-o", "none")
        ctx.state.save(vault_officer_assigned=True)
    started = time.monotonic()
    while az("keyvault", "secret", "list", "--vault-name", vault, "-o", "none", check=False).returncode != 0:
        if time.monotonic() - started > 900:
            raise RuntimeError("Key Vault Secrets Officer did not take effect within 15 minutes.")
        time.sleep(10)
    ctx.state.save(vault_officer_propagation_s=elapsed(started))


def setup_identity(ctx):
    rg = ctx.rg
    if "identity_id" not in ctx.state:
        log(f"Managed identity {ctx.state['identity']}")
        view = az_json("identity", "create", "-g", rg, "-n", ctx.state["identity"], "-l", ctx.region, *tags())
        ctx.state.save(identity_id=view["id"])
        ctx.secret.save(identity_client_id=view["clientId"], identity_principal_id=view["principalId"])
    sensitive("identity client id", ctx.secret["identity_client_id"])
    sensitive("identity principal id", ctx.secret["identity_principal_id"])
    if "workspace_id" not in ctx.state:
        log(f"Log Analytics workspace {ctx.state['workspace']}")
        view = az_json("monitor", "log-analytics", "workspace", "create", "-g", rg, "-n", ctx.state["workspace"],
                       "-l", ctx.region, *tags())
        ctx.state.save(workspace_id=view["id"])
        ctx.secret.save(workspace_customer_id=view["customerId"])
    sensitive("workspace customer id", ctx.secret["workspace_customer_id"])


def setup_registry(ctx, commit):
    rg, registry = ctx.rg, ctx.state["registry"]
    if "registry_id" not in ctx.state:
        log(f"Container registry {registry} (Basic)")
        view = az_json("acr", "create", "-g", rg, "-n", registry, "-l", ctx.region, *tags(), "--sku", "Basic",
                       "--admin-enabled", "false")
        ctx.state.save(registry_id=view["id"], registry_server=view["loginServer"])
    if "image" not in ctx.state:
        work = Path(tempfile.mkdtemp(prefix="atli-ps-acr-", dir=ctx.work))
        try:
            context = spike.build_context(work)
            image = f"atli-reports-server:{commit}"
            log(f"az acr build {image} from the server Dockerfile at {commit}")
            started = time.monotonic()
            result = az("acr", "build", "--registry", registry, "--image", image, "--file", str(context / "Dockerfile"),
                        "--platform", "linux/amd64", str(context), check=False, timeout=3600)
            (ctx.work / "logs/acr-build.log").write_text(redact(result.stdout[-20000:] + result.stderr[-5000:]))
            if result.returncode != 0:
                raise RuntimeError("az acr build failed; see logs/acr-build.log")
            ctx.state.save(image=f"{ctx.state['registry_server']}/{image}", acr_build_s=elapsed(started))
        finally:
            shutil.rmtree(work, ignore_errors=True)


def setup_sandbox_groups(ctx, networks, commit):
    def create(key):
        aca = ctx.aca(key)
        if f"propagation_{key}" not in ctx.state:
            created = aca("sandboxgroup", "create", "--name", ctx.group(key), "--location", ctx.region, check=False)
            if created.returncode != 0 and "exist" not in created.stderr.lower():
                raise RuntimeError(redact(f"sandboxgroup create {key}: {created.stderr[-800:]}"))
            started = time.monotonic()
            while aca("sandbox", "list", "-o", "json", check=False).returncode != 0:
                if time.monotonic() - started > 1200:
                    raise RuntimeError("The data-plane role did not propagate within 20 minutes.")
                time.sleep(10)
            ctx.state.save(**{f"propagation_{key}": elapsed(started)})
            log(f"Sandbox group {ctx.group(key)}: data plane works after {ctx.state[f'propagation_{key}']} s")

    with concurrent.futures.ThreadPoolExecutor(3) as pool:
        list(pool.map(create, ["vnet", "control", "client"]))
    networks.result()
    if "network_connection" not in ctx.state:
        log("Connecting the renderer group to the renderer network (connection 'renderers')")
        started = time.monotonic()
        ctx.aca("vnet")("sandboxgroup", "network", "create", "--group", ctx.group("vnet"),
                        "--vnet-subnet-id", ctx.state["renderer_subnet_id"], "--name", "renderers", timeout=1800)
        ctx.state.save(network_connection="renderers", network_connection_s=elapsed(started))

    def build(spec):
        key, suffix = spec
        name = f"reports-server-{commit}-{suffix}"
        state_key = f"disk_{key}_{suffix}"
        if state_key in ctx.state:
            return
        aca = ctx.aca(key)
        work = Path(tempfile.mkdtemp(prefix="atli-ps-context-", dir=ctx.work))
        try:
            context = spike.build_context(work)
            started = time.monotonic()
            for attempt in range(6):
                result = aca("sandboxgroup", "disk", "create", "--source", str(context), "--name", name,
                             "--wait-timeout", "3000", check=False, timeout=3600)
                if result.returncode == 0:
                    break
                if "403" not in result.stderr or attempt == 5:
                    raise RuntimeError(f"Disk image {name} failed:\n{result.stderr[-1500:]}")
                time.sleep(30)
            seconds = elapsed(started)
        finally:
            shutil.rmtree(work, ignore_errors=True)
        disks = json.loads(aca("sandboxgroup", "disk", "list", "-o", "json").stdout)
        disk = next(d for d in disks if d.get("name") == name)
        ctx.state.save(**{state_key: disk["id"], f"{state_key}_name": name, f"{state_key}_build_s": seconds})
        log(f"Disk {name} in {key}: {seconds} s")

    with concurrent.futures.ThreadPoolExecutor(3) as pool:
        list(pool.map(build, [("vnet", "a"), ("vnet", "b"), ("control", "a")]))


def setup_roles(ctx):
    principal = ctx.secret["identity_principal_id"]
    if "role_definition_id" not in ctx.state:
        definition = {
            "Name": ctx.state["role_name"], "IsCustom": True,
            "Description": "Temporary test role: read and resume sandboxes only.",
            "Actions": [], "NotActions": [],
            "DataActions": ["Microsoft.App/sandboxGroups/sandboxes/read",
                            "Microsoft.App/sandboxGroups/sandboxes/resume/action"],
            "NotDataActions": [], "AssignableScopes": [ctx.rg_id()],
        }
        path = ctx.work / "role.json"
        path.write_text(json.dumps(definition))
        path.chmod(0o600)
        created = az_json("role", "definition", "create", "--role-definition", f"@{path}")
        path.unlink()
        ctx.state.save(role_definition_id=created["id"], role_definition_name=created["name"])
    assignments = ctx.state.get("assignments", {})
    for name, role, scope in [("acr_pull", "AcrPull", ctx.state["registry_id"]),
                              ("vault_user", "Key Vault Secrets User", ctx.state["vault_id"]),
                              ("resumer", ctx.state["role_definition_name"], ctx.group_id("vnet"))]:
        if name in assignments:
            continue
        for attempt in range(30):  # a new role definition takes a while to be assignable
            result = az("role", "assignment", "create", "--assignee-object-id", principal,
                        "--assignee-principal-type", "ServicePrincipal", "--role", role, "--scope", scope,
                        "--query", "id", "-o", "tsv", check=False)
            if result.returncode == 0:
                break
            time.sleep(15)
        else:
            raise RuntimeError(f"Role assignment {name} failed: {redact(result.stderr[-500:])}")
        assignments[name] = result.stdout.strip()
        ctx.state.save(assignments=assignments, **{f"assigned_{name}_utc": utc()})
    log("Gateway identity: AcrPull, Key Vault Secrets User, and the resume-only role assigned")


def arm(method, path, body=None, api=ARM_APP_API, check=True):
    """An ARM call through az rest; the body travels in a 0600 file that is deleted at once."""
    args = ["rest", "--method", method, "--url", f"https://management.azure.com{path}?api-version={api}"]
    handle = None
    if body is not None:
        handle = tempfile.NamedTemporaryFile("w", delete=False, suffix=".json", dir=CONTEXT_WORK[0])
        os.chmod(handle.name, 0o600)
        handle.write(json.dumps(body))
        handle.close()
        args += ["--body", f"@{handle.name}"]
    try:
        result = az(*args, "-o", "json", check=check)
    finally:
        if handle:
            os.unlink(handle.name)
    return json.loads(result.stdout or "null") if result.returncode == 0 else None


CONTEXT_WORK = [None]


def setup_environment(ctx):
    path = f"{ctx.rg_id()}/providers/Microsoft.App/managedEnvironments/{ctx.state['environment']}"
    ctx.state.save(environment_id=path)
    view = arm("GET", path, check=False)
    if not view:
        log(f"Container Apps environment {ctx.state['environment']} (workload profiles, gateway subnet)")
        key = az("monitor", "log-analytics", "workspace", "get-shared-keys", "-g", ctx.rg, "-n",
                 ctx.state["workspace"], "--query", "primarySharedKey", "-o", "tsv").stdout.strip()
        sensitive("workspace key", key)
        arm("PUT", path, {
            "location": ctx.region, "tags": {"purpose": PURPOSE},
            "properties": {
                "vnetConfiguration": {"infrastructureSubnetId": ctx.state["gateway_subnet_id"], "internal": False},
                "workloadProfiles": [{"name": "Consumption", "workloadProfileType": "Consumption"}],
                "infrastructureResourceGroup": ctx.state["infra_rg"],
                "zoneRedundant": False,
                "appLogsConfiguration": {"destination": "log-analytics", "logAnalyticsConfiguration": {
                    "customerId": ctx.secret["workspace_customer_id"], "sharedKey": key}},
            }})
    started = time.monotonic()
    while True:
        view = arm("GET", path)
        state = view["properties"].get("provisioningState")
        if state == "Succeeded":
            break
        if state in ("Failed", "Canceled") or time.monotonic() - started > 2400:
            raise RuntimeError(f"Environment {state}")
        time.sleep(15)
    ctx.state.save(environment_domain=view["properties"].get("defaultDomain"))
    # The platform creates the infrastructure group; tag it as ours where the platform allows.
    if az("group", "exists", "-n", ctx.state["infra_rg"]).stdout.strip() == "true":
        tagged = az("group", "update", "-n", ctx.state["infra_rg"], "--set", f"tags.purpose={PURPOSE}", "-o", "none",
                    check=False)
        ctx.state.save(infra_rg_tagged=tagged.returncode == 0)


# ---------------------------------------------------------------------------------------------
# The gateway
# ---------------------------------------------------------------------------------------------

def caller_credentials(ctx):
    credentials = ctx.work / "caller"
    if not credentials.exists():
        run(["bash", str(REPO / "scripts/create-reports-api-key.sh"), str(credentials), CALLER])
    server = dict(line.split("=", 1) for line in (credentials / "server.env").read_text().split())
    requests = ctx.work / "requests"
    if not requests.exists():
        requests.mkdir(mode=0o700)
        spike.write_requests(requests, credentials)
    key = (requests / "api-key").read_text().strip()
    sensitive("caller key", key)
    sensitive("caller key verifier", server["ReportsServer__Authentication__ApiKeys__0__Hash"])
    return server, key


def gateway_env(ctx, log_requests, debug_gateway):
    sandboxes = {"SubscriptionId": ctx.subscription, "ResourceGroup": ctx.rg, "SandboxGroup": ctx.group("vnet"),
                 "Region": ctx.region, "ManagedIdentityClientId": ctx.secret["identity_client_id"]}
    env = {
        "ReportsServer__Mode": "Gateway",
        "ReportsServer__Authentication__Mode": "ApiKey",
        "ReportsServer__Authentication__ApiKeys__0__CallerId": CALLER,
        "ReportsServer__Authentication__ApiKeys__0__Permissions__0": "reports.convert",
        # One caller converts for every tenant here; the tenant limit, not the caller's, is measured.
        "ReportsServer__Limits__MaxConcurrentRequestsPerCaller": "64",
        "ReportsServer__Gateway__Tenants__0__CallerId": CALLER,
        "ReportsServer__Gateway__Records__Store": "KeyVault",
        "ReportsServer__Gateway__Records__VaultUri": ctx.state["vault_uri"],
        "ReportsServer__Gateway__Records__ManagedIdentityClientId": ctx.secret["identity_client_id"],
        "ReportsServer__Gateway__Wake__Mode": "Sandboxes",
        "Logging__Console__FormatterName": "simple",
        "Logging__Console__FormatterOptions__SingleLine": "true",
        "Logging__Console__FormatterOptions__UseUtcTimestamp": "true",
        "Logging__Console__FormatterOptions__TimestampFormat": "HH:mm:ss.fff ",
    }
    for index, tenant in enumerate(TENANTS):
        env[f"ReportsServer__Gateway__Tenants__0__Tenants__{index}"] = tenant
    for name, value in sandboxes.items():
        env[f"ReportsServer__Gateway__Wake__Sandboxes__{name}"] = value
    if log_requests:
        # "Request starting" and "Request finished ... in N ms" for every request.
        env["Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information"
    if debug_gateway:
        # Event 56: each resend to a busy renderer.
        env["Logging__LogLevel__Atli.Reports.Server.Gateway.RendererGateway"] = "Debug"
    return env


def app_path(ctx):
    return f"{ctx.rg_id()}/providers/Microsoft.App/containerApps/{APP}"


def deploy_gateway(ctx, replicas=1, log_requests=False, debug_gateway=False):
    """Creates or updates the gateway app; returns once its latest revision answers /health/ready."""
    server, _ = caller_credentials(ctx)
    settings = {"replicas": replicas, "log_requests": log_requests, "debug_gateway": debug_gateway}
    if ctx.state.get("gateway_settings") == settings and ctx.state.get("gateway_fqdn"):
        if http_get(f"https://{ctx.state['gateway_fqdn']}/health/ready")[0] == 200:
            return
    env = gateway_env(ctx, log_requests, debug_gateway)
    env_list = [{"name": name, "value": value} for name, value in env.items()]
    env_list += [{"name": "ReportsServer__Authentication__ApiKeys__0__Id", "secretRef": "caller-key-id"},
                 {"name": "ReportsServer__Authentication__ApiKeys__0__Hash", "secretRef": "caller-key-verifier"}]
    identity = ctx.state["identity_id"]
    body = {
        "location": ctx.region, "tags": {"purpose": PURPOSE},
        "identity": {"type": "UserAssigned", "userAssignedIdentities": {identity: {}}},
        "properties": {
            "environmentId": ctx.state["environment_id"],
            "workloadProfileName": "Consumption",
            "configuration": {
                "activeRevisionsMode": "Single",
                "ingress": {"external": True, "targetPort": 8080, "transport": "auto", "allowInsecure": False},
                "registries": [{"server": ctx.state["registry_server"], "identity": identity}],
                "secrets": [
                    {"name": "caller-key-id", "value": server["ReportsServer__Authentication__ApiKeys__0__Id"]},
                    {"name": "caller-key-verifier", "value": server["ReportsServer__Authentication__ApiKeys__0__Hash"]},
                ],
            },
            "template": {
                "containers": [{
                    "name": "gateway", "image": ctx.state["image"],
                    "resources": {"cpu": 1.0, "memory": "2Gi"},
                    "env": env_list,
                    "probes": [
                        {"type": "Liveness", "httpGet": {"path": "/health/live", "port": 8080},
                         "initialDelaySeconds": 1, "periodSeconds": 10, "timeoutSeconds": 3, "failureThreshold": 3},
                        {"type": "Readiness", "httpGet": {"path": "/health/ready", "port": 8080},
                         "initialDelaySeconds": 1, "periodSeconds": 5, "timeoutSeconds": 10, "failureThreshold": 3},
                    ],
                }],
                "scale": {"minReplicas": replicas, "maxReplicas": replicas},
            },
        },
    }
    log(f"Deploying the gateway: {settings}")
    started = time.monotonic()
    arm("PUT", app_path(ctx), body)
    revision = None
    while True:
        view = arm("GET", app_path(ctx))
        properties = view["properties"]
        if (properties.get("provisioningState") == "Succeeded"
                and properties.get("latestReadyRevisionName") == properties.get("latestRevisionName")):
            revision = properties["latestRevisionName"]
            fqdn = properties["configuration"]["ingress"]["fqdn"]
            break
        if properties.get("provisioningState") == "Failed" or time.monotonic() - started > 1200:
            raise RuntimeError(f"Gateway deployment {properties.get('provisioningState')}")
        time.sleep(5)
    ctx.state.save(gateway_fqdn=fqdn, gateway_revision=revision, gateway_settings=settings)
    sensitive("gateway host", fqdn)
    ready_after = None
    while time.monotonic() - started < 1200:
        if http_get(f"https://{fqdn}/health/ready")[0] == 200:
            ready_after = elapsed(started)
            break
        time.sleep(2)
    if ready_after is None:
        raise RuntimeError("The gateway did not answer /health/ready")
    replicas_seen = wait_replicas(ctx, revision, replicas)
    deploys = ctx.state.get("gateway_deploys", [])
    deploys.append(dict(settings, revision=revision, ready_after_s=ready_after, replicas_running=replicas_seen,
                        at_utc=utc()))
    ctx.state.save(gateway_deploys=deploys)
    log(f"Gateway revision {revision} ready after {ready_after} s with {replicas_seen} replica(s)")


def wait_replicas(ctx, revision, want, limit=600):
    started = time.monotonic()
    count = 0
    while time.monotonic() - started < limit:
        view = arm("GET", f"{app_path(ctx)}/revisions/{revision}/replicas", check=False) or {}
        running = [r for r in view.get("value", [])
                   if all(c.get("ready") for c in r.get("properties", {}).get("containers", []))]
        count = len(running)
        if count >= want:
            return count
        time.sleep(5)
    return count


def restart_gateway(ctx):
    revision = ctx.state["gateway_revision"]
    started = time.monotonic()
    arm("POST", f"{app_path(ctx)}/revisions/{revision}/restart")
    time.sleep(10)
    while http_get(f"https://{ctx.state['gateway_fqdn']}/health/ready")[0] != 200:
        if time.monotonic() - started > 600:
            raise RuntimeError("The gateway did not come back after the restart.")
        time.sleep(2)
    wait_replicas(ctx, revision, ctx.state["gateway_settings"]["replicas"])
    return elapsed(started)


def gateway_logs(ctx, since_utc, until_utc=None, contains=None, limit=5000):
    """The gateway's console lines between two UTC times, from Log Analytics (ingestion lags a few
    minutes). Lines name tenants and sandboxes only."""
    query = (f"ContainerAppConsoleLogs_CL | where ContainerAppName_s == '{APP}' "
             f"| where TimeGenerated >= datetime({since_utc})"
             + (f" and TimeGenerated <= datetime({until_utc})" if until_utc else "")
             + (" | where " + " or ".join(f"Log_s has '{c}'" for c in contains) if contains else "")
             + f" | project TimeGenerated, ContainerGroupName_s, RevisionName_s, Log_s | order by TimeGenerated asc"
             + f" | take {limit}")
    rows = az_json("monitor", "log-analytics", "query", "-w", ctx.secret["workspace_customer_id"],
                   "--analytics-query", query, "-t", "P1D", check=False) or []
    return [{"time": row.get("TimeGenerated"), "replica": row.get("ContainerGroupName_s"),
             "revision": row.get("RevisionName_s"), "line": redact(row.get("Log_s", ""))} for row in rows]


def gateway_count(ctx, since_utc, until_utc, kql_tail):
    query = (f"ContainerAppConsoleLogs_CL | where ContainerAppName_s == '{APP}' "
             f"| where TimeGenerated between (datetime({since_utc}) .. datetime({until_utc})) | {kql_tail}")
    return az_json("monitor", "log-analytics", "query", "-w", ctx.secret["workspace_customer_id"],
                   "--analytics-query", query, "-t", "P1D", check=False) or []


# ---------------------------------------------------------------------------------------------
# The client sandbox
# ---------------------------------------------------------------------------------------------

def setup_client(ctx):
    aca, rest = ctx.aca("client"), ctx.rest("client")
    if "client_sandbox" not in ctx.state:
        log("Client sandbox (python-3.12, egress only to the gateway's host and the port proxy)")
        size = ("4000m", "8192Mi")
        result = aca("sandbox", "create", "--disk", "python-3.12", "--cpu", size[0], "--memory", size[1],
                     "--egress-default", "Deny", "--egress-rule", f"{ctx.state['gateway_fqdn']}:Allow",
                     "--egress-rule", "*.adcproxy.io:Allow", "--label", "role=prod-shape-client", "-o", "json",
                     check=False)
        if result.returncode != 0:
            size = ("2000m", "4096Mi")
            result = aca("sandbox", "create", "--disk", "python-3.12", "--cpu", size[0], "--memory", size[1],
                         "--egress-default", "Deny", "--egress-rule", f"{ctx.state['gateway_fqdn']}:Allow",
                         "--egress-rule", "*.adcproxy.io:Allow", "--label", "role=prod-shape-client", "-o", "json")
        match = re.search(rf"Created sandbox: ({spike.UUID})", result.stdout)
        ctx.state.save(client_sandbox=match.group(1), client_size=list(size))
    client = ctx.state["client_sandbox"]
    rest.request("POST", f"sandboxes/{client}/lifecycle", {"autoSuspendPolicy": {"enabled": False, "interval": 0}})
    rest.ensure_running(client)
    view = rest.sandbox(client) or {}
    outbound = view.get("outboundIpAddresses") or []
    ctx.secret.save(client_outbound_ips=outbound)
    for index, address in enumerate(outbound):
        sensitive(f"client outbound ip {index}", address)
    _, key = caller_credentials(ctx)
    upload_client(ctx)
    info = aca.exec(client, "nproc; python3 --version; grep MemTotal /proc/meminfo").stdout.split()
    ctx.state.save(client_view={"size": ctx.state["client_size"], "info": info,
                                "outbound_addresses": len(outbound)})


def upload_client(ctx):
    aca, client = ctx.aca("client"), ctx.state["client_sandbox"]
    requests = ctx.work / "requests"
    aca.write(client, "/root/run/gateway-client.py", HERE / "gateway-client.py")
    for name in ["invoice.json", "long-table.json", "api-key"]:
        aca.write(client, f"/root/run/{name}", requests / name)
    dummy = ctx.work / "dummy-key"
    dummy.write_text("reports-000000000000." + "0" * 64)
    aca.write(client, "/root/run/dummy-key", dummy)
    aca.exec(client, "chmod 600 /root/run/api-key /root/run/dummy-key")


def client_once(ctx, url, tenant=None, body="invoice.json", key="api-key", method="POST", path="/convert"):
    command = (f"cd /root/run && python3 gateway-client.py once --url {url} --key-file {key} "
               + (f"--tenant {tenant} " if tenant else "") + (f"--body {body} " if body else "")
               + f"--method {method} --path {path}")
    output = ctx.aca("client").exec(ctx.state["client_sandbox"], command, check=False).stdout
    line = next((l for l in output.splitlines() if l.startswith("{")), None)
    return json.loads(line) if line else {"status": "no output", "output": redact(output[-300:])}


def gateway_url(ctx):
    return f"https://{ctx.state['gateway_fqdn']}"


def client_job(ctx, name, command, timeout):
    """Starts a command in the client in the background (an aca exec request ends after about a
    minute) and polls its output file for a JSON line."""
    aca, client = ctx.aca("client"), ctx.state["client_sandbox"]
    out = f"/root/run/{name}.out"
    aca.exec(client, f"cd /root/run && rm -f {out} {out}.err && setsid nohup sh -c '{command}' > {out} "
                     f"2> {out}.err < /dev/null &")
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        time.sleep(10)
        text = aca.exec(client, f"cat {out}; echo; tail -c 600 {out}.err", check=False).stdout
        found = next((l for l in text.splitlines() if l.startswith("{")), None)
        if found:
            return json.loads(found)
        if "Traceback" in text:
            raise RuntimeError(f"client job {name} failed: {redact(text[-600:])}")
    raise RuntimeError(f"client job {name}: no result within {timeout} s")


def client_file(ctx, path):
    text = ctx.aca("client").exec(ctx.state["client_sandbox"], f"cat {path}", check=False).stdout
    return json.loads(text.strip().splitlines()[-1] if text.strip() else "null")


# ---------------------------------------------------------------------------------------------
# The provisioner and records
# ---------------------------------------------------------------------------------------------

class Provisioner:
    """The provisioner for one sandbox group, configured by Provisioner__* variables; each command's
    output, with the second each line appeared, goes to --work/logs, redacted."""

    def __init__(self, ctx, key):
        self.ctx, self.key = ctx, key
        env = {k: v for k, v in os.environ.items() if not k.startswith("Provisioner__")}
        env.update({
            "Provisioner__Sandboxes__SubscriptionId": ctx.subscription,
            "Provisioner__Sandboxes__ResourceGroup": ctx.rg,
            "Provisioner__Sandboxes__SandboxGroup": ctx.group(key),
            "Provisioner__Sandboxes__Region": ctx.region,
            "Provisioner__Records__Store": "KeyVault",
            "Provisioner__Records__VaultUri": ctx.state["vault_uri"],
            "Provisioner__DiskImageId": ctx.state[f"disk_{key}_a"],
            "Provisioner__Size": "M",
            "Provisioner__PortActivation": "OnDemand",
            "Provisioner__AllowedSourceCidrs__0": f"{ctx.secret['nat_ip']}/32",
            "Provisioner__AllowedSourceCidrs__1": f"{ctx.secret['laptop_ip']}/32",
        })
        if key == "vnet":
            env["Provisioner__NetworkConnection"] = "renderers"
        self.env = env

    def __call__(self, *args, overrides=None, check=True):
        count = self.ctx.state.get("provisioner_count", 0) + 1
        self.ctx.state.save(provisioner_count=count)
        started = time.monotonic()
        process = subprocess.Popen(
            ["dotnet", str(BIN / "Atli.Reports.Provisioner/release/atli-reports-provisioner.dll"), *args],
            env=dict(self.env, **(overrides or {})), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        lines = [(elapsed(started), redact(line.rstrip("\n"))) for line in process.stdout]
        code = process.wait()
        seconds = elapsed(started)
        path = self.ctx.work / "logs" / f"provisioner-{count:03d}-{self.key}-{args[0]}.log"
        path.write_text(f"$ provisioner {' '.join(args)}\n"
                        + "".join(f"[{at:7.2f}] {line}\n" for at, line in lines)
                        + f"exit {code} after {seconds} s\n")
        log(f"provisioner ({self.key}) {' '.join(args)}: exit {code} in {seconds} s")
        if check and code != 0:
            raise RuntimeError(f"provisioner {' '.join(args)} exited {code}:\n"
                               + "\n".join(l for _, l in lines[-15:]))
        return {"exit": code, "seconds": seconds, "lines": lines}


def scrub_url(line):
    return re.sub(r"https://\S+\.adcproxy\.io/?", "<port URL>", line)


def read_record(ctx, tenant):
    """The tenant's record from Key Vault, credential included (kept in memory, never printed)."""
    result = az("keyvault", "secret", "show", "--vault-name", ctx.state["vault"], "--name", f"renderer-{tenant}",
                "--query", "value", "-o", "tsv", check=False)
    if result.returncode != 0:
        return None
    record = json.loads(result.stdout)
    sensitive(f"renderer key {tenant} {record.get('sandboxId', '')[:8]}", record["apiKey"])
    keys = ctx.secret.get("renderer_keys", [])
    if record["apiKey"] not in keys:
        ctx.secret.save(renderer_keys=keys + [record["apiKey"]])
    return record


def create_line_times(lines, tenant):
    """From a create's output: seconds of the create call and of the whole launch to ready."""
    times = {}
    for at, line in lines:
        if line.startswith(f"[{tenant}] Creating a sandbox"):
            times["create_started_at"] = at
        elif line.startswith(f"[{tenant}] Created sandbox"):
            times["create_call_s"] = round(at - times.get("create_started_at", at), 2)
        elif line.startswith(f"[{tenant}] Ready "):
            times["ready_after_create_call_s"] = float(line.split()[2])
    return times


# ---------------------------------------------------------------------------------------------
# tenants
# ---------------------------------------------------------------------------------------------

def tenants(ctx):
    check_laptop_ip(ctx)
    build_tools()
    results = ctx.results.get("tenants", {})
    provisioners = {"vnet": Provisioner(ctx, "vnet"), "control": Provisioner(ctx, "control")}
    for tenant, (group, size, overrides) in TENANTS.items():
        if tenant in results and results[tenant].get("exit") == 0:
            continue
        overrides = dict(overrides)
        if group == "control":
            for index, address in enumerate(ctx.secret.get("client_outbound_ips", [])):
                overrides[f"Provisioner__AllowedSourceCidrs__{2 + index}"] = f"{address}/32"
        result = provisioners[group]("create", "--tenant", tenant, "--size", size, overrides=overrides, check=False)
        record = read_record(ctx, tenant) if result["exit"] == 0 else None
        view = ctx.rest(group).sandbox(record["sandboxId"]) if record else None
        port = next((p for p in (view or {}).get("ports", []) if p.get("port") == 8080), {})
        results[tenant] = {
            "group": group, "size": size, "exit": result["exit"], "command_s": result["seconds"],
            **create_line_times(result["lines"], tenant),
            "sandbox": record and record["sandboxId"],
            "max_concurrent_requests": record and record.get("maxConcurrentRequests"),
            "network_connection": (view or {}).get("customerVnetConnectionName"),
            "port": {"activationMode": port.get("activationMode"),
                     "ip_default_action": (port.get("ipAccessControl") or {}).get("defaultAction"),
                     "ip_rules": len((port.get("ipAccessControl") or {}).get("rules") or []),
                     "ip_ranges": sum(len(r.get("sourceCidrs") or []) for r in
                                      (port.get("ipAccessControl") or {}).get("rules") or [])},
            "lifecycle": (view or {}).get("lifecycle"),
            "lines": [scrub_url(line) for _, line in result["lines"] if "URL:" not in line][-12:],
        }
        ctx.record("tenants", results)
    listed = provisioners["vnet"]("list", check=False)
    ctx.record("tenants", dict(results, list_vnet=[scrub_url(l) for _, l in listed["lines"]]))


def build_tools():
    for project in ["Atli.Reports.Provisioner", "Atli.Reports.Server"]:
        run(["dotnet", "build", str(REPO / "src" / project), "-c", "Release", "-nologo", "-v", "q"], timeout=900)


# ---------------------------------------------------------------------------------------------
# hardening
# ---------------------------------------------------------------------------------------------

def hardening(ctx):
    check_laptop_ip(ctx)
    results = {}
    url = gateway_url(ctx)
    for tenant, group in [("vm1", "vnet"), ("cm1", "control")]:
        record = read_record(ctx, tenant)
        rest, aca = ctx.rest(group), ctx.aca(group)
        sandbox = record["sandboxId"]
        # Wake it through the gateway, as the service would.
        woke = client_once(ctx, url, tenant)
        rest.ensure_running(sandbox)
        for name in ["dns-probe.pl", "network-probe.sh", "chromium-sandbox-check.sh"]:
            aca.write(sandbox, f"/tmp/probe/{name}", HERE / name)
        # Without DNS every lookup waits for its timeout, and an exec request ends after about a
        # minute: run the probes in the background and poll for their end. Files `fs write` makes
        # belong to root and commands run as the server's user, so the output goes to /tmp itself.
        script = ("echo '== resolv.conf'; grep -v '^#' /etc/resolv.conf; echo '== dns'; perl /tmp/probe/dns-probe.pl; "
                  "echo '== network'; bash /tmp/probe/network-probe.sh; echo '== chromium';"
                  " sh /tmp/probe/chromium-sandbox-check.sh; echo \"chromium_check_exit=$?\"; echo '== vm';"
                  " uname -r; nproc; grep MemTotal /proc/meminfo; id -u; echo '== end'")
        (ctx.work / "client/probe.sh").write_text(script + "\n")
        aca.write(sandbox, "/tmp/probe/probe.sh", ctx.work / "client/probe.sh")
        started = time.monotonic()
        aca.exec(sandbox, "rm -f /tmp/probe-out; setsid nohup sh /tmp/probe/probe.sh > /tmp/probe-out 2>&1 "
                          "< /dev/null &")
        output = ""
        while time.monotonic() - started < 600 and "== end" not in output:
            time.sleep(10)
            output = aca.exec(sandbox, "cat /tmp/probe-out", check=False).stdout
        probe_s = elapsed(started)
        sections, current = {}, None
        for line in output.splitlines():
            if line.startswith("== "):
                current = line[3:].strip()
                sections[current] = []
            elif current and line.strip():
                sections[current].append(redact(line.strip()))
        view = rest.sandbox(sandbox) or {}
        convert = {"invoice": client_once(ctx, url, tenant), "long_table": client_once(ctx, url, tenant, "long-table.json")}
        status, seconds, body = http_get(f"{record['url'].rstrip('/')}/health/ready")
        results[tenant] = {
            "group": group, "sandbox": sandbox, "network_connection": view.get("customerVnetConnectionName"),
            "egress_default": (view.get("egressPolicy") or {}).get("defaultAction"),
            "waking_invoice": woke, "probes": sections, "probes_s": probe_s, "through_gateway": convert,
            "port_ready_from_laptop": {"status": status, "s": round(seconds, 3)},
        }
        ctx.record("hardening", results)
    # The client is not among vm1's allowed sources: its request is refused and wakes nothing.
    record = read_record(ctx, "vm1")
    rest, sandbox = ctx.rest("vnet"), record["sandboxId"]
    stop = rest.stop(sandbox)
    time.sleep(3)
    port_url = record["url"].rstrip("/")
    refused = {"convert": client_once(ctx, port_url, None, "invoice.json", "dummy-key"),
               "health_live": client_once(ctx, port_url, None, None, "dummy-key", "GET", "/health/live")}
    time.sleep(10)
    results["ip_refusal"] = {"stop": stop, "client_requests": refused,
                             "vm1_state_10s_later": rest.state_of(sandbox)}
    # And the same request through the gateway (the NAT gateway's address is allowed) wakes it.
    results["ip_refusal"]["through_gateway_after"] = client_once(ctx, url, "vm1")
    results["verdicts"] = hardening_verdicts(results)
    ctx.record("hardening", results)
    for check, verdict in results["verdicts"].items():
        log(f"{check}: {verdict}")


def hardening_verdicts(r):
    def check(condition):
        try:
            return "pass" if condition() else "FAIL"
        except (KeyError, IndexError, TypeError, ValueError, StopIteration):
            return "not run"

    def probe(tenant, section, key):
        return next(l.split("=", 1)[1] for l in r[tenant]["probes"][section] if l.startswith(key + "="))

    dns_keys = ["udp53_10_42_0_4_A_example_com", "udp53_10_42_0_4_TXT_example_com",
                "udp53_10_42_0_4_A_random_example_com", "udp53_10_42_0_4_TXT_microsoft_com",
                "udp53_168_63_129_16_A_example_com", "udp53_8_8_8_8_A_example_com", "udp53_1_1_1_1_A_example_com"]
    ok = lambda result: result.get("status") == 200 and result.get("pdf") is True  # noqa: E731
    return {
        "vnet_resolver_is_vnet_setting": check(lambda: probe("vm1", "dns", "resolver") == "10.42.0.4"),
        "vnet_no_name_resolves": check(lambda: all(probe("vm1", "dns", k) in ("timeout", "error") for k in dns_keys)),
        "vnet_getent_fails": check(lambda: probe("vm1", "network", "dns_resolves_example_com") == "no"),
        "vnet_egress_denied": check(lambda: probe("vm1", "network", "http_1_1_1_1") in ("403", "")
                                    and probe("vm1", "network", "tcp_8_8_8_8_53") == "blocked"),
        "vnet_imds_blocked": check(lambda: probe("vm1", "network", "tcp_imds_169_254_169_254_80") == "blocked"),
        "vnet_chromium_sandbox": check(lambda: probe("vm1", "chromium", "chromium_check_exit") == "0"),
        "vnet_conversions": check(lambda: ok(r["vm1"]["through_gateway"]["invoice"])
                                  and ok(r["vm1"]["through_gateway"]["long_table"])),
        "vnet_port_through_proxy": check(lambda: r["vm1"]["port_ready_from_laptop"]["status"] == 200),
        "control_dns_resolves": check(lambda: probe("cm1", "dns", "udp53_168_63_129_16_A_example_com").startswith("NOERROR")),
        "control_chromium_sandbox": check(lambda: probe("cm1", "chromium", "chromium_check_exit") == "0"),
        "client_refused_by_address": check(lambda: r["ip_refusal"]["client_requests"]["convert"]["status"] == 403
                                           and r["ip_refusal"]["client_requests"]["convert"]["kind"] == "IpAccessDenied"),
        "refused_request_wakes_nothing": check(lambda: r["ip_refusal"]["vm1_state_10s_later"] == "Stopped"),
    }


# ---------------------------------------------------------------------------------------------
# creates
# ---------------------------------------------------------------------------------------------

def renderer_environment(size):
    """A renderer environment as RendererServerEnvironment builds it, with a throwaway credential."""
    key_id = "reports-" + random_secrets.token_hex(6)
    credential = key_id + "." + random_secrets.token_hex(32)
    verifier = base64.b64encode(hashlib.sha256(credential.encode()).digest()).decode()
    conversions = SIZES[size][2]
    admitted = 2 * conversions
    return {
        "ReportsServer__Authentication__Mode": "ApiKey",
        "ReportsServer__Authentication__ApiKeys__0__Id": key_id,
        "ReportsServer__Authentication__ApiKeys__0__Hash": verifier,
        "ReportsServer__Authentication__ApiKeys__0__CallerId": "gateway",
        "ReportsServer__Authentication__ApiKeys__0__Permissions__0": "reports.convert",
        "ReportsServer__Limits__MaxConcurrentRequestsPerCaller": str(admitted),
        "ReportsServer__Limits__MaxRequestBodyBytes": str(3 * 10 * 1024 * 1024),
        "Kestrel__Limits__MaxRequestBodySize": str(3 * 10 * 1024 * 1024),
        "ReportsEngine__Concurrency__MaxConcurrentConversions": str(conversions),
        "ReportsEngine__Concurrency__MaxQueueLength": str(admitted - conversions),
        "ReportsEngine__Network__Mode": "Disabled",
    }


def creates(ctx):
    check_laptop_ip(ctx)
    results = ctx.results.get("creates", {"samples": []})
    cidrs = [f"{ctx.secret['nat_ip']}/32", f"{ctx.secret['laptop_ip']}/32"]
    rounds = ctx.options.samples
    for index in range(len(results["samples"]) // 2, rounds):
        order = ["vnet", "control"] if index % 2 == 0 else ["control", "vnet"]
        for key in order:
            rest = ctx.rest(key)
            body = {
                "sourcesRef": {"diskImage": {"id": ctx.state[f"disk_{key}_a"], "isPublic": False}},
                "resources": {"cpu": SIZES["M"][0], "memory": SIZES["M"][1]},
                "egressPolicy": {"defaultAction": "Deny"},
                "entrypoint": spike.ENTRYPOINT,
                "environment": renderer_environment("M"),
                "labels": {"app": "atli-reports-probe", "role": "create-probe"},
                "lifecycle": {"autoSuspendPolicy": {"enabled": True, "interval": 300, "mode": "Memory"}},
            }
            if key == "vnet":
                body["customerVnetConnectionName"] = "renderers"
            started = time.monotonic()
            status, view, create_s = rest.timed("PUT", "sandboxes", body)
            if status >= 300:
                results["samples"].append({"group": key, "round": index, "create_status": status,
                                           "error": redact(json.dumps(view))[:300]})
                ctx.record("creates", results)
                continue
            sandbox = view["id"]
            try:
                port = {"port": 8080, "auth": {"anonymous": True}, "activationMode": "OnDemand",
                        "ipAccessControl": {"defaultAction": "Deny", "rules": [
                            {"name": "allowed-0", "action": "Allow", "priority": 100, "sourceCidrs": cidrs}]}}
                status, ports, port_s = rest.timed("POST", f"sandboxes/{sandbox}/ports/add", port)
                url = next(p["url"] for p in (ports.get("ports") if isinstance(ports, dict) else ports)
                           if p["port"] == 8080)
                ready_s, statuses = None, {}
                while time.monotonic() - started < 300:
                    code = http_get(f"{url.rstrip('/')}/health/ready", timeout=5)[0]
                    statuses[str(code)] = statuses.get(str(code), 0) + 1
                    if code == 200:
                        ready_s = elapsed(started)
                        break
                    time.sleep(0.1)
                state = view.get("state")
            finally:
                _, _, delete_s = rest.timed("DELETE", f"sandboxes/{sandbox}")
            sample = {"group": key, "round": index, "create_call_s": round(create_s, 2), "state_returned": state,
                      "port_call_s": round(port_s, 2), "ready_after_create_start_s": ready_s,
                      "probe_statuses": statuses, "delete_call_s": round(delete_s, 2)}
            results["samples"].append(sample)
            log(f"create {key}: call {sample['create_call_s']} s, ready {ready_s} s")
            ctx.record("creates", results)
    for key in ["vnet", "control"]:
        samples = [s for s in results["samples"] if s["group"] == key and s.get("ready_after_create_start_s")]
        results[f"summary_{key}"] = {
            "n": len(samples),
            "create_call_s": sorted(s["create_call_s"] for s in samples),
            "ready_after_create_start_s": sorted(s["ready_after_create_start_s"] for s in samples),
        }
    ctx.record("creates", results)


# ---------------------------------------------------------------------------------------------
# wake
# ---------------------------------------------------------------------------------------------

def wake(ctx):
    check_laptop_ip(ctx)
    results = ctx.results.get("wake", {})
    url = gateway_url(ctx)
    deploy_gateway(ctx, replicas=1, log_requests=True)
    records = {t: read_record(ctx, t) for t in ["vm1", "cm1", "vauto", "vman"]}
    groups = {"vm1": "vnet", "cm1": "control", "vauto": "vnet", "vman": "vnet"}
    if ctx.options.wake_pair:
        # A second connected renderer against the same control one, invoices only.
        tenant = ctx.options.wake_pair
        records[tenant], groups[tenant] = read_record(ctx, tenant), TENANTS[tenant][0]
        key = f"on_demand_{tenant}"
        results[key] = on_demand_cycles(ctx, url, records, groups, results.get(key, []), (tenant, "cm1"),
                                        ("invoice.json",))
        ctx.record("wake", results)
        return

    # Auto-suspend cycles for vauto, alongside the OnDemand cycles.
    def auto_suspend():
        rest, sandbox = ctx.rest("vnet"), records["vauto"]["sandboxId"]
        cycles = []
        for _ in range(ctx.options.auto_cycles):
            before = client_once(ctx, url, "vauto")
            last = time.monotonic()
            while rest.state_of(sandbox) != "Stopped" and time.monotonic() - last < 600:
                time.sleep(2)
            observed = elapsed(last)
            details = (rest.sandbox(sandbox) or {}).get("stateDetails")
            time.sleep(5)
            woke = client_once(ctx, url, "vauto")
            after = client_once(ctx, url, "vauto")
            cycles.append({"request_before": before, "stopped_observed_after_s": observed, "state_details": details,
                           "waking_invoice": woke, "next_invoice": after})
            log(f"auto-suspend: Stopped {observed} s after the last request; wake {woke.get('s')} s")
        return cycles

    if "auto_suspend" not in results:
        with concurrent.futures.ThreadPoolExecutor(1) as pool:
            auto = pool.submit(auto_suspend)
            results["on_demand"] = on_demand_cycles(ctx, url, records, groups, results.get("on_demand", []))
            ctx.record("wake", results)
            results["auto_suspend"] = auto.result()
        ctx.record("wake", results)

    # Manual port: the gateway's resume through its identity and the resume-only role.
    if "manual" not in results or len(results["manual"].get("cycles", [])) < ctx.options.manual_cycles:
        rest, sandbox = ctx.rest("vnet"), records["vman"]["sandboxId"]
        rest.ensure_running(sandbox)
        restart_s = restart_gateway(ctx)
        since = utc()
        time.sleep(5)
        cycles = []
        for index in range(ctx.options.manual_cycles):
            stop = rest.stop(sandbox)
            time.sleep(5)
            sent_utc = utc()
            woke = client_once(ctx, url, "vman")
            after = client_once(ctx, url, "vman")
            cycles.append(dict(stop, sent_utc=sent_utc, waking_invoice=woke, next_invoice=after,
                               state_after=rest.state_of(sandbox)))
            log(f"manual wake {index}: {woke.get('status')} in {woke.get('s')} s")
            time.sleep(35)  # past the record cache, as between the OnDemand cycles
        results["manual"] = {"gateway_restart_s": restart_s, "since_utc": since, "cycles": cycles}
        ctx.record("wake", results)
    if "log" not in results["manual"]:
        results["manual"]["log"] = manual_log(ctx, results["manual"])
        ctx.record("wake", results)
    results["summary"] = wake_summary(results)
    ctx.record("wake", results)


def on_demand_cycles(ctx, url, records, groups, done, pair=("vm1", "cm1"),
                     fixtures=("invoice.json", "long-table.json")):
    cycles = list(done)
    for index in range(len(cycles), len(fixtures) * ctx.options.wake_cycles):
        fixture = fixtures[index % len(fixtures)]
        stops = {}
        for tenant in pair:  # one that auto-suspended meanwhile cannot be stopped (409)
            ctx.rest(groups[tenant]).ensure_running(records[tenant]["sandboxId"])
        with concurrent.futures.ThreadPoolExecutor(2) as pool:
            futures = {t: pool.submit(ctx.rest(groups[t]).stop, records[t]["sandboxId"]) for t in pair}
            for tenant, future in futures.items():
                stops[tenant] = future.result()
        time.sleep(5)
        order = list(pair) if (index // len(fixtures)) % 2 == 0 else list(reversed(pair))
        entry = {"cycle": index, "fixture": fixture, "order": order, "tenants": {}}
        for tenant in order:
            woke = client_once(ctx, url, tenant, fixture)
            after = client_once(ctx, url, tenant)
            entry["tenants"][tenant] = dict(stops[tenant], waking=woke, next_invoice=after,
                                            state_after=ctx.rest(groups[tenant]).state_of(records[tenant]["sandboxId"]))
        log(f"on-demand {index} {fixture}: " + ", ".join(
            f"{t} {entry['tenants'][t]['waking'].get('status')} {entry['tenants'][t]['waking'].get('s')} s" for t in order))
        cycles.append(entry)
        time.sleep(30)  # past the gateway's record cache, so every wake also reads the record again
    return cycles


def manual_log(ctx, manual):
    """Events 60 and 61 and the request lines of the Manual cycles, waiting for ingestion."""
    deadline = time.monotonic() + 900
    lines = []
    while time.monotonic() < deadline:
        lines = gateway_logs(ctx, manual["since_utc"], contains=["[60]", "[61]", "[62]", "[63]", "Request starting HTTP/1.1 POST",
                                                                 "Request finished HTTP/1.1 POST", "vman"])
        if sum("[61]" in l["line"] for l in lines) >= len(manual["cycles"]):
            break
        time.sleep(30)
    return lines


def wake_summary(results):
    summary = {}
    for tenant in ["vm1", "cm1"]:
        for fixture in ["invoice.json", "long-table.json"]:
            values = [c["tenants"][tenant]["waking"].get("s") for c in results.get("on_demand", [])
                      if c["fixture"] == fixture and c["tenants"][tenant]["waking"].get("status") == 200]
            summary[f"{tenant}_{fixture.split('.')[0]}"] = {"n": len(values), "s": sorted(values)}
    summary["vauto_after_auto_suspend"] = sorted(c["waking_invoice"].get("s") for c in results.get("auto_suspend", [])
                                                 if c["waking_invoice"].get("status") == 200)
    summary["vman_manual"] = [c["waking_invoice"].get("s") for c in results.get("manual", {}).get("cycles", [])]
    return summary


# ---------------------------------------------------------------------------------------------
# warm
# ---------------------------------------------------------------------------------------------

def warm(ctx):
    results = ctx.results.get("warm", {})
    url = gateway_url(ctx)
    deploy_gateway(ctx, replicas=1)
    record = read_record(ctx, "cm1")
    key_file = ctx.work / "cm1-key"
    key_file.write_text(record["apiKey"])
    key_file.chmod(0o600)
    aca, client = ctx.aca("client"), ctx.state["client_sandbox"]
    aca.write(client, "/root/run/cm1-key", key_file)
    aca.exec(client, "chmod 600 /root/run/cm1-key")
    key_file.unlink()
    try:
        for fixture in ["invoice", "long-table"]:
            if fixture in results:
                continue
            targets = [{"name": f"{t}_gateway", "url": url, "key_file": "api-key", "tenant": t, "body": f"{fixture}.json"}
                       for t in ["vs1", "vm1", "vl1", "cm1"]]
            targets.append({"name": "cm1_direct", "url": record["url"].rstrip("/"), "key_file": "cm1-key",
                            "body": f"{fixture}.json"})
            for target in targets:  # wake every renderer first
                client_once(ctx, target["url"], target.get("tenant"), target["body"], target["key_file"])
            plan = ctx.work / "client/latency.json"
            plan.write_text(json.dumps({"samples": ctx.options.samples, "targets": targets}))
            aca.write(client, f"/root/run/latency-{fixture}.json", plan)
            results[fixture] = client_job(ctx, f"latency-{fixture}",
                                          f"python3 gateway-client.py latency --plan latency-{fixture}.json", 1800)
            for name, entry in results[fixture].items():
                log(f"warm {fixture} {name}: {entry['summary']}")
            ctx.record("warm", results)
    finally:
        aca.exec(client, "rm -f /root/run/cm1-key", check=False)
    overhead = {}
    for fixture in ["invoice", "long-table"]:
        through, direct = results[fixture]["cm1_gateway"]["summary"], results[fixture]["cm1_direct"]["summary"]
        overhead[fixture] = {k: round(through[k] - direct[k], 4) for k in ["p50", "p95", "mean"]}
    results["gateway_overhead_cm1_s"] = overhead
    ctx.record("warm", results)


# ---------------------------------------------------------------------------------------------
# load
# ---------------------------------------------------------------------------------------------

def sampler_start(ctx, tenant, seconds, label):
    group = TENANTS[tenant][0]
    record = read_record(ctx, tenant)
    ctx.rest(group).ensure_running(record["sandboxId"])
    ctx.aca(group).exec(record["sandboxId"], (
        f"rm -f /tmp/sample-{label}; setsid nohup sh -c '{followup.SAMPLER.format(seconds=int(seconds))}' "
        f"> /tmp/sample-{label} 2>&1 < /dev/null &"))
    return record["sandboxId"]


def sampler_read(ctx, tenant, sandbox, label):
    group = TENANTS[tenant][0]
    for _ in range(30):
        text = ctx.aca(group).exec(sandbox, f"cat /tmp/sample-{label}", check=False).stdout
        if "peak_mib" in text:
            return dict(pair.split("=", 1) for pair in text.split() if "=" in pair)
        time.sleep(10)
    return {"error": "no sample"}


def metrics(ctx, since_utc, until_utc):
    """The gateway's CPU and memory per minute and replica, from Container Apps metrics."""
    out = {}
    for metric in ["UsageNanoCores", "WorkingSetBytes", "CpuPercentage", "MemoryPercentage", "Requests",
                   "RestartCount", "ResponseTime"]:
        dimension = {"Requests": "statusCodeCategory", "ResponseTime": "statusCodeCategory"}.get(metric, "podName")
        view = az_json("monitor", "metrics", "list", "--resource", app_path(ctx), "--metric", metric,
                       "--interval", "PT1M", "--aggregation", "Average", "Maximum", "Total",
                       "--start-time", since_utc, "--end-time", until_utc,
                       "--filter", f"{dimension} eq '*'", check=False) or {}
        series = {}
        for entry in (view.get("value") or [{}])[0].get("timeseries", []):
            name = ",".join(m["value"] for m in entry.get("metadatavalues", [])) or "all"
            series[name] = [{k: point.get(k) for k in ["timeStamp", "average", "maximum", "total"]}
                            for point in entry.get("data", [])]
        out[metric] = series
    return out


def load_phase(ctx, label, duration, factor, replicas, rollout=False):
    url = gateway_url(ctx)
    deploy_gateway(ctx, replicas=replicas, debug_gateway=True)
    sizes = {t: TENANTS[t][1] for t in LOAD_TENANTS}
    plan = {"url": url, "key_file": "api-key", "duration": duration,
            "bodies": {"invoice": "invoice.json", "long-table": "long-table.json"},
            "tenants": [{"tenant": t, "concurrency": ADMITTED[sizes[t]] * factor, "report_every": 20}
                        for t in LOAD_TENANTS]}
    log(f"load {label}: {duration} s, {replicas} replica(s), concurrency {[t['concurrency'] for t in plan['tenants']]}")
    for tenant in LOAD_TENANTS:  # wake every renderer
        client_once(ctx, url, tenant)
    samplers = {} if rollout else {t: sampler_start(ctx, t, duration + 150, label) for t in LOAD_TENANTS}
    path = ctx.work / "client/load.json"
    path.write_text(json.dumps(plan))
    aca, client = ctx.aca("client"), ctx.state["client_sandbox"]
    aca.write(client, f"/root/run/load-{label}.json", path)
    since = utc()
    started = time.monotonic()
    aca.exec(client, f"cd /root/run && rm -rf out-{label} && setsid nohup python3 gateway-client.py load "
                     f"--plan load-{label}.json --out out-{label} > load-{label}.out 2>&1 < /dev/null &")
    start_epoch = None
    while start_epoch is None and time.monotonic() - started < 120:
        text = aca.exec(client, f"cat /root/run/out-{label}/started", check=False).stdout.strip()
        start_epoch = float(text) if re.fullmatch(r"[0-9.]+", text or "") else None
        time.sleep(2)
    rollout_result = None
    if rollout:
        time.sleep(max(0.0, start_epoch + 60 - time.time()))
        rollout_started = time.time() - start_epoch
        provisioner = Provisioner(ctx, "vnet")
        rolled = provisioner("rollout", "--disk-image", ctx.state["disk_vnet_b"], "--max-parallel", "4", check=False)
        rollout_result = {"started_at_s": round(rollout_started, 2), "ended_at_s": round(time.time() - start_epoch, 2),
                          "exit": rolled["exit"], "seconds": rolled["seconds"],
                          "lines": [f"[{at:7.2f}] {scrub_url(line)}" for at, line in rolled["lines"] if "URL:" not in line]}
    deadline = time.monotonic() + duration + 900
    summary = None
    while time.monotonic() < deadline:
        time.sleep(15)
        text = aca.exec(client, f"cat /root/run/load-{label}.out", check=False).stdout
        if '"done": true' in text:
            summary = client_file(ctx, f"/root/run/out-{label}/summary.json")
            break
        if "Traceback" in text:
            raise RuntimeError(redact(text[-800:]))
    until = utc()
    result = {"label": label, "duration_s": duration, "factor": factor, "replicas": replicas,
              "since_utc": since, "until_utc": until, "client": summary, "rollout": rollout_result,
              "gateway_revision": ctx.state["gateway_revision"]}
    result["renderer_vm"] = {t: sampler_read(ctx, t, sandbox, label) for t, sandbox in samplers.items()}
    ctx.record(f"load_{label}", result)
    # Metrics and logs arrive a few minutes late.
    time.sleep(240)
    result["gateway_metrics"] = metrics(ctx, since, utc())
    result["gateway_events"] = gateway_events(ctx, since, utc())
    ctx.record(f"load_{label}", result)
    return result


def gateway_events(ctx, since, until):
    rows = gateway_count(ctx, since, until,
                         "extend event = extract(@'\\[(\\d+)\\]', 1, Log_s), tenant = extract(@'(?i)tenant ([a-z0-9-]+)', 1, Log_s), "
                         "status = extract(@'answered (\\d+)', 1, Log_s) | where isnotempty(event) "
                         "| summarize count() by event, tenant, status, ContainerGroupName_s")
    return [{"event": r.get("event"), "tenant": r.get("tenant"), "status": r.get("status"),
             "replica": r.get("ContainerGroupName_s"), "count": int(r.get("count_", 0))} for r in rows]


def load(ctx):
    check_laptop_ip(ctx)
    load_phase(ctx, ctx.options.label, ctx.options.duration, ctx.options.factor, ctx.options.replicas)


def rollout(ctx):
    check_laptop_ip(ctx)
    for tenant, group in [("cm1", "control"), ("vman", "vnet"), ("vauto", "vnet")]:
        if read_record(ctx, tenant):
            Provisioner(ctx, group)("delete", "--tenant", tenant, check=False)
    result = load_phase(ctx, "rollout", ctx.options.duration, 1, ctx.options.replicas, rollout=True)
    listed = Provisioner(ctx, "vnet")("list", check=False)
    pruned = Provisioner(ctx, "vnet")("prune", "--drain", "00:00:00", check=False)
    result["after"] = {"list": [scrub_url(l) for _, l in listed["lines"]],
                       "prune": [l for _, l in pruned["lines"]]}
    for tenant in LOAD_TENANTS:
        read_record(ctx, tenant)  # the new credentials, for verify
    ctx.record("load_rollout", result)


# ---------------------------------------------------------------------------------------------
# verify and cleanup
# ---------------------------------------------------------------------------------------------

def verify(ctx, extra_paths=()):
    """Searches every log, the results, and the given files for each sensitive value."""
    values = dict(SENSITIVE)
    for key, value in ctx.secret.items():
        if isinstance(value, str) and len(value) >= 6:
            values.setdefault(f"<{key}>", value)
        elif isinstance(value, list):
            for index, item in enumerate(value):
                values.setdefault(f"<{key} {index}>", str(item))
    files = sorted((ctx.work / "logs").glob("*")) + ([ctx.output] if ctx.output else []) + [Path(p) for p in extra_paths]
    found = {}
    for path in files:
        text = path.read_text(errors="replace") if path.exists() else ""
        for name, value in values.items():
            if value and value in text:
                found.setdefault(str(path.name), []).append(name)
        for pattern, name in [(r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.", "token"),
                              (r"reports-[0-9a-f]{12}\.[0-9a-f]{64}", "api key")]:
            if re.search(pattern, text):
                found.setdefault(str(path.name), []).append(name)
    result = {"files": len(files), "values_checked": len(values), "found": found or "none"}
    log(f"verify: {result['files']} files, {result['values_checked']} values, found {result['found']}")
    return result


def guard(ctx, name):
    """Refuses to delete a resource group that is not this run's: the prefix and the tag."""
    if not name.startswith(PREFIX):
        raise RuntimeError(f"Refusing to delete {name}: not {PREFIX}*")
    tag = az("group", "show", "-n", name, "--query", "tags.purpose", "-o", "tsv", check=False).stdout.strip()
    return tag == PURPOSE


def cleanup(ctx):
    results = {"started_utc": utc()}
    if "resource_group" not in ctx.state:
        log("Nothing to clean up.")
        return
    rg = ctx.rg
    exists = az("group", "exists", "-n", rg).stdout.strip() == "true"
    if exists and not guard(ctx, rg):
        raise RuntimeError(f"{rg} lacks the purpose tag; not deleting it.")
    if exists:
        # The provisioner deletes its tenants (records and sandboxes).
        tenants_left = []
        if ctx.state.get("vault_uri"):
            for tenant, (group, _, _) in TENANTS.items():
                if read_record(ctx, tenant):
                    Provisioner(ctx, group)("delete", "--tenant", tenant, check=False)
                    tenants_left.append(tenant)
        results["tenants_deleted"] = tenants_left
        # Role assignments, then the role definition (it cannot go while assigned).
        for name, assignment in (ctx.state.get("assignments") or {}).items():
            az("role", "assignment", "delete", "--ids", assignment, check=False)
        if ctx.state.get("vault_officer_assigned"):
            az("role", "assignment", "delete", "--assignee", ctx.secret["my_object_id"], "--role",
               "Key Vault Secrets Officer", "--scope", ctx.state["vault_id"], check=False)
        if ctx.state.get("role_definition_name"):
            az("role", "definition", "delete", "--name", ctx.state["role_definition_name"], "--scope", ctx.rg_id(),
               check=False)
        # The workspace, permanently: deleted with its group it would stay soft-deleted for 14 days.
        if ctx.state.get("workspace_id"):
            az("monitor", "log-analytics", "workspace", "delete", "-g", rg, "-n", ctx.state["workspace"],
               "--force", "true", "--yes", check=False)
        # The gateway app and environment, so the platform removes the infrastructure group.
        if ctx.state.get("environment_id"):
            arm("DELETE", app_path(ctx), check=False)
            arm("DELETE", ctx.state["environment_id"], check=False)
        # Sandbox groups: the connected one's network connection first, so the subnet is free.
        for key in ["client", "control", "vnet"]:
            group = ctx.state.get(f"group_{key}")
            if not group or ctx.state.get(f"propagation_{key}") is None:
                continue
            aca = ctx.aca(key)
            if key == "vnet" and ctx.state.get("network_connection"):
                aca("sandboxgroup", "network", "delete", "--group", group, "--name", "renderers", "--yes",
                    check=False, timeout=3600)
            started = time.monotonic()
            deleted = aca("sandboxgroup", "delete", "--name", group, "--yes", check=False, timeout=3600)
            results[f"sandbox_group_{key}_delete"] = {"exit": deleted.returncode, "s": elapsed(started)}
            log(f"Sandbox group {group} deleted: exit {deleted.returncode} in {elapsed(started)} s")
        started = time.monotonic()
        log(f"Deleting {rg}")
        az("group", "delete", "-n", rg, "--yes", "--no-wait", check=False)
    started = time.monotonic()
    for name in [rg, ctx.state.get("infra_rg")]:
        while name and az("group", "exists", "-n", name, check=False).stdout.strip() != "false":
            if time.monotonic() - started > 5400:
                raise RuntimeError(f"{name} still exists")
            time.sleep(20)
    results["resource_groups_gone_after_s"] = elapsed(started)
    # The vault went to soft-delete with its group: purge it.
    vault = ctx.state.get("vault")
    if vault:
        for _ in range(30):
            deleted = az_json("keyvault", "list-deleted", "--query", f"[?name=='{vault}'].name", check=False) or []
            if deleted:
                az("keyvault", "purge", "--name", vault, "--location", ctx.region, check=False, timeout=1800)
                break
            time.sleep(10)
        remaining = az_json("keyvault", "list-deleted", "--query", f"[?name=='{vault}'].name", check=False) or []
        results["vault_purged"] = not remaining and not az_json("keyvault", "list", "--query",
                                                                  f"[?name=='{vault}'].name", check=False)
    checks = {
        "resource_group_exists": az("group", "exists", "-n", rg).stdout.strip(),
        "infra_resource_group_exists": az("group", "exists", "-n", ctx.state.get("infra_rg", rg)).stdout.strip(),
        "groups_with_prefix": az_json("group", "list", "--query", f"[?starts_with(name, '{PREFIX}')].name"),
        "resources_with_tag": len(az_json("resource", "list", "--tag", f"purpose={PURPOSE}", "--query", "[].name") or []),
        "role_definition_left": bool(ctx.state.get("role_definition_name") and arm(
            "GET", f"/subscriptions/{ctx.subscription}/providers/Microsoft.Authorization/roleDefinitions/"
                   f"{ctx.state['role_definition_name']}", api="2022-04-01", check=False)),
        "role_definitions_named": len(az_json("role", "definition", "list", "--custom-role-only", "true", "--query",
                                              f"[?roleName=='{ctx.state.get('role_name')}'].name", check=False) or []),
        "assignments_for_identity": len(az_json("role", "assignment", "list", "--all", "--assignee",
                                                ctx.secret.get("identity_principal_id", "none"), "--query", "[].id",
                                                check=False) or []),
        "identity_exists": az("identity", "show", "--ids", ctx.state.get("identity_id", "/none"), "-o", "none",
                              check=False).returncode == 0,
        "deleted_workspaces": len(az_json("monitor", "log-analytics", "workspace", "list-deleted-workspaces",
                                          "--query", f"[?name=='{ctx.state.get('workspace')}'].name", check=False) or []),
    }
    results["checks"] = checks
    results["finished_utc"] = utc()
    ctx.record("cleanup", results)
    log(f"cleanup checks: {checks}")


# ---------------------------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("command", choices=["setup", "tenants", "hardening", "creates", "wake", "warm", "load",
                                            "rollout", "verify", "cleanup", "gateway"])
    parser.add_argument("--work", type=Path, required=True, help="state, secrets, and logs (mode 0700)")
    parser.add_argument("--output", type=Path, required=True, help="results JSON (redacted)")
    parser.add_argument("--location", default="eastus2")
    parser.add_argument("--aca", type=Path, help="the aca binary, when it is not on PATH")
    parser.add_argument("--samples", type=int, default=20, help="creates: rounds; warm: samples per target")
    parser.add_argument("--wake-cycles", type=int, default=5, help="wake: OnDemand cycles per fixture")
    parser.add_argument("--auto-cycles", type=int, default=2)
    parser.add_argument("--manual-cycles", type=int, default=3)
    parser.add_argument("--wake-pair", help="wake: only OnDemand invoice cycles of this connected tenant and cm1")
    parser.add_argument("--duration", type=int, default=900)
    parser.add_argument("--factor", type=int, default=1, help="load: times each tenant's admitted concurrency")
    parser.add_argument("--replicas", type=int, default=1)
    parser.add_argument("--label", default="r1")
    parser.add_argument("--log-requests", action="store_true", help="gateway: request logging")
    parser.add_argument("--debug-gateway", action="store_true", help="gateway: event 56")
    options = parser.parse_args()
    if options.aca:
        os.environ["PATH"] = f"{options.aca.resolve().parent}{os.pathsep}{os.environ['PATH']}"
    for tool in ["az", "aca", "dotnet", "git", "openssl", "curl"]:
        if not shutil.which(tool):
            sys.exit(f"Missing required tool: {tool}")
    ctx = Context(options)
    CONTEXT_WORK[0] = str(ctx.work)
    if options.command == "gateway":
        deploy_gateway(ctx, options.replicas, options.log_requests, options.debug_gateway)
    elif options.command == "verify":
        ctx.record("verify", verify(ctx))
    else:
        {"load": load}.get(options.command, globals()[options.command])(ctx)


if __name__ == "__main__":
    main()
