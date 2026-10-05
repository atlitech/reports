"""Shared, dependency-injected infrastructure for the Azure sandbox benchmark runners.

Importing this module performs no CLI or network calls. Scenario runtimes own their secrets,
token caches, command execution and HTTP transport; contexts own state and resource names.
"""
import base64
import hashlib
import http.client
import json
import os
import re
import secrets as random_secrets
import shutil
import ssl
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

import run as spike

REPO = spike.REPO
API_VERSION = spike.API_VERSION
ARM_APP_API = "2024-03-01"
DATA_PLANE_RESOURCE = "https://dynamicsessions.io"
SIZES = {"S": ("500m", "1024Mi", 1), "M": ("1000m", "2048Mi", 1), "L": ("2000m", "4096Mi", 2)}
_PORT_URL = re.compile(r"https?://[A-Za-z0-9.-]+\.adcproxy\.io[^\s\"',)]*")
_IPV4 = re.compile(r"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d.])")


def private_ipv4(match):
    a, b = int(match.group(1)), int(match.group(2))
    text = match.group(0)
    return (a in (10, 127, 0) or (a == 172 and 16 <= b <= 31) or (a == 192 and b == 168)
            or (a == 169 and b == 254) or text == "168.63.129.16" or a > 255)


class Redactor:
    def __init__(self, public_addresses=False):
        self.values = {}
        self.lock = threading.Lock()
        self.public_addresses = public_addresses

    def sensitive(self, name, value):
        if value and len(str(value)) >= 6:
            with self.lock:
                # Keep previous tokens too, so delayed log collection still scrubs expired ones.
                label = f"<{name}>"
                index = 1
                while label in self.values and self.values[label] != str(value):
                    index += 1
                    label = f"<{name} {index}>"
                self.values[label] = str(value)

    def __call__(self, text):
        text = str(text)
        with self.lock:
            items = sorted(self.values.items(), key=lambda item: -len(item[1]))
        for name, value in items:
            text = text.replace(value, name)
        text = re.sub(r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", "<token>", text)
        text = re.sub(r"reports-[0-9a-f]{12}\.[0-9a-f]{64}", "<api key>", text)
        if self.public_addresses:
            text = _PORT_URL.sub("<port URL>", text)
            text = _IPV4.sub(lambda m: m.group(0) if private_ipv4(m) else "<ip>", text)
        return text


class TokenCache:
    """Cache by audience until five minutes before the CLI token's actual expiry."""
    def __init__(self, acquire, sensitive, clock=time.time):
        self.acquire, self.sensitive, self.clock = acquire, sensitive, clock
        self.tokens = {}
        self.lock = threading.Lock()

    def get(self, resource, name):
        # Serialize refreshes so concurrent setup tasks do not all invoke the CLI at once.
        with self.lock:
            cached = self.tokens.get(resource)
            if cached and self.clock() < cached[1] - 300:
                return cached[0]
            view = self.acquire(resource)
            token = view["accessToken"]
            # Older responses without expires_on are usable for this request, but never cached.
            expires = float(view.get("expires_on") or 0)
            self.sensitive(name, token)
            self.tokens[resource] = (token, expires)
            return token


class Runtime:
    def __init__(self, *, public_addresses=False, command_runner=subprocess.run,
                 opener=urllib.request.urlopen, clock=time.time):
        self.redactor = Redactor(public_addresses)
        self.sensitive = self.redactor.sensitive
        self.redact = self.redactor
        self.command_runner, self.opener = command_runner, opener
        self.tokens = TokenCache(
            lambda resource: self.az_json("account", "get-access-token", "--resource", resource),
            self.sensitive, clock)

    def log(self, message):
        print(f"[{time.strftime('%H:%M:%S')}] {self.redact(message)}", flush=True)

    def run(self, command, check=True, env=None, timeout=1800, input=None):
        result = self.command_runner(command, capture_output=True, text=True, env=env, timeout=timeout,
                                     check=False, input=input)
        if check and result.returncode != 0:
            raise RuntimeError(self.redact(f"{' '.join(command[:4])} ... exited {result.returncode}:\n"
                                           f"{result.stderr[-1500:]}"))
        return result

    def az(self, *args, check=True, timeout=1800):
        return self.run(["az", *args], check=check, timeout=timeout)

    def az_json(self, *args, check=True, timeout=1800):
        result = self.az(*args, "-o", "json", check=check, timeout=timeout)
        return json.loads(result.stdout or "null") if result.returncode == 0 else None

    def access_token(self, resource, name):
        return self.tokens.get(resource, name)

    def data_plane_token(self):
        return self.access_token(DATA_PLANE_RESOURCE, "data-plane token")


def utc():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def elapsed(since):
    return round(time.monotonic() - since, 2)


class Store(dict):
    """A JSON file of mode 0600 in --work."""

    def __init__(self, path):
        super().__init__(json.loads(path.read_text()) if path.exists() else {})
        self.path = path
        self.lock = threading.RLock()

    def save(self, **values):
        with self.lock:
            self.update(values)
            self.path.write_text(json.dumps(self, indent=2) + "\n")
            self.path.chmod(0o600)


class Context:
    def __init__(self, options, runtime):
        self.runtime = runtime
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
            account = self.runtime.az_json("account", "show")
            self.secret.save(subscription=account["id"], tenant=account["tenantId"])
        self.subscription = self.secret["subscription"]
        self.runtime.sensitive("subscription", self.subscription)
        self.runtime.sensitive("tenant id", self.secret["tenant"])
        for key, value in self.secret.items():
            if key not in ("subscription", "tenant") and isinstance(value, str):
                self.runtime.sensitive(key, value)
            elif isinstance(value, list):
                for index, item in enumerate(value):
                    self.runtime.sensitive(f"{key} {index}", item)

    def record(self, key, value):
        self.results[key] = value
        if self.output:
            self.output.parent.mkdir(parents=True, exist_ok=True)
            self.output.write_text(self.runtime.redact(json.dumps(self.results, indent=2)) + "\n")
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
        return Aca(self.subscription, self.rg, self.group(key), self.region, self.runtime)

    def rest(self, key):
        return Rest(self.subscription, self.rg, self.group(key), self.region, self.runtime)

    def arm(self, method, path, body=None, api=ARM_APP_API, check=True):
        """An ARM call through az rest; the body travels in a 0600 file that is deleted at once."""
        args = ["rest", "--method", method, "--url", f"https://management.azure.com{path}?api-version={api}"]
        handle = None
        if body is not None:
            handle = tempfile.NamedTemporaryFile("w", delete=False, suffix=".json", dir=self.work)
            os.chmod(handle.name, 0o600)
            handle.write(json.dumps(body))
            handle.close()
            args += ["--body", f"@{handle.name}"]
        try:
            result = self.runtime.az(*args, "-o", "json", check=check)
        finally:
            if handle:
                os.unlink(handle.name)
        return json.loads(result.stdout or "null") if result.returncode == 0 else None


class Aca:
    def __init__(self, subscription, resource_group, group, region, runtime):
        self.runtime = runtime
        self.env = dict(os.environ, ACA_SUBSCRIPTION=subscription, ACA_RESOURCE_GROUP=resource_group,
                        ACA_SANDBOX_GROUP=group, ACA_REGION=region)

    def __call__(self, *args, check=True, timeout=1800):
        return self.runtime.run(["aca", *args], check=check, env=self.env, timeout=timeout)

    def exec(self, sandbox, command, check=True, timeout=900):
        return self("sandbox", "exec", "--id", sandbox, "-c", command, check=check, timeout=timeout)

    def write(self, sandbox, path, file):
        self("sandbox", "fs", "write", "--id", sandbox, "--path", path, "--file", str(file))


class Rest:
    """The Sandboxes data plane over HTTPS with a cached token: (status, body) per call."""

    def __init__(self, subscription, resource_group, group, region, runtime):
        self.runtime = runtime
        self.base = (f"https://management.{region}.azuredevcompute.io/subscriptions/{subscription}"
                     f"/resourceGroups/{resource_group}/sandboxGroups/{group}")

    def request(self, method, path, body=None, timeout=300):
        url = f"{self.base}/{path}?api-version={API_VERSION}" if path else f"{self.base}?api-version={API_VERSION}"
        request = urllib.request.Request(url, method=method,
                                         data=None if body is None else json.dumps(body).encode(),
                                         headers={"Authorization": f"Bearer {self.runtime.data_plane_token()}",
                                                  "Content-Type": "application/json"})
        try:
            with self.runtime.opener(request, timeout=timeout) as response:
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


def laptop_ip(runtime):
    address = runtime.run(["curl", "-fsS", "-4", "https://api.ipify.org"]).stdout.strip()
    if not re.fullmatch(r"\d{1,3}(\.\d{1,3}){3}", address):
        raise RuntimeError("Could not read this machine's public IPv4 address.")
    return address


def check_laptop_ip(ctx):
    address = laptop_ip(ctx.runtime)
    if ctx.secret.get("laptop_ip") and address != ctx.secret["laptop_ip"]:
        raise RuntimeError("This machine's public address changed since setup; the ports no longer admit it.")
    ctx.secret.save(laptop_ip=address)
    ctx.runtime.sensitive("laptop ip", address)


def setup_networks(ctx, purpose):
    az, az_json = ctx.runtime.az, ctx.runtime.az_json
    log, sensitive = ctx.runtime.log, ctx.runtime.sensitive
    tags = ["--tags", f"purpose={purpose}"]
    rg = ctx.rg
    if "renderer_subnet_id" not in ctx.state:
        log("Renderer network: DNS server 10.42.0.4 (unused), subnet 10.42.1.0/24, NSG denying AzurePlatformDNS")
        az("network", "nsg", "create", "-g", rg, "-n", "nsg-renderers", "-l", ctx.region, *tags, "-o", "none")
        az("network", "nsg", "rule", "create", "-g", rg, "--nsg-name", "nsg-renderers", "-n", "deny-azure-platform-dns",
           "--priority", "100", "--direction", "Outbound", "--access", "Deny", "--protocol", "*",
           "--source-address-prefixes", "*", "--source-port-ranges", "*",
           "--destination-address-prefixes", "AzurePlatformDNS", "--destination-port-ranges", "*", "-o", "none")
        az("network", "vnet", "create", "-g", rg, "-n", "vnet-renderers", "-l", ctx.region, *tags,
           "--address-prefixes", "10.42.0.0/16", "--dns-servers", "10.42.0.4", "-o", "none")
        subnet = az_json("network", "vnet", "subnet", "create", "-g", rg, "--vnet-name", "vnet-renderers",
                         "-n", "renderers", "--address-prefixes", "10.42.1.0/24",
                         "--delegations", "Microsoft.App/environments", "--network-security-group", "nsg-renderers")
        ctx.state.save(renderer_subnet_id=subnet["id"])
    if "gateway_subnet_id" not in ctx.state:
        log("Gateway network: subnet 10.60.0.0/27 with a NAT gateway and a static Standard public address")
        az("network", "public-ip", "create", "-g", rg, "-n", "pip-gateway", "-l", ctx.region, *tags,
           "--sku", "Standard", "--allocation-method", "Static", "--version", "IPv4", "-o", "none")
        az("network", "nat", "gateway", "create", "-g", rg, "-n", "nat-gateway", "-l", ctx.region, *tags,
           "--public-ip-addresses", "pip-gateway", "--idle-timeout", "4", "-o", "none")
        az("network", "vnet", "create", "-g", rg, "-n", "vnet-gateway", "-l", ctx.region, *tags,
           "--address-prefixes", "10.60.0.0/16", "-o", "none")
        subnet = az_json("network", "vnet", "subnet", "create", "-g", rg, "--vnet-name", "vnet-gateway",
                         "-n", "gateway", "--address-prefixes", "10.60.0.0/27",
                         "--delegations", "Microsoft.App/environments", "--nat-gateway", "nat-gateway")
        ctx.state.save(gateway_subnet_id=subnet["id"])
    nat_ip = az("network", "public-ip", "show", "-g", rg, "-n", "pip-gateway", "--query", "ipAddress",
                "-o", "tsv").stdout.strip()
    ctx.secret.save(nat_ip=nat_ip)
    sensitive("nat ip", nat_ip)


def create_sandbox_group(ctx, key):
    aca = ctx.aca(key)
    if f"propagation_{key}" in ctx.state:
        return
    created = aca("sandboxgroup", "create", "--name", ctx.group(key), "--location", ctx.region, check=False)
    if created.returncode != 0 and "exist" not in created.stderr.lower():
        raise RuntimeError(ctx.runtime.redact(f"sandboxgroup create {key}: {created.stderr[-800:]}"))
    started = time.monotonic()
    while aca("sandbox", "list", "-o", "json", check=False).returncode != 0:
        if time.monotonic() - started > 1200:
            raise RuntimeError("The data-plane role did not propagate within 20 minutes.")
        time.sleep(10)
    ctx.state.save(**{f"propagation_{key}": elapsed(started)})
    ctx.runtime.log(f"Sandbox group {ctx.group(key)}: data plane works after {ctx.state[f'propagation_{key}']} s")


def connect_renderer_network(ctx, key):
    if "network_connection" in ctx.state:
        return
    ctx.runtime.log("Connecting the renderer group to the renderer network (connection 'renderers')")
    started = time.monotonic()
    ctx.aca(key)("sandboxgroup", "network", "create", "--group", ctx.group(key),
                 "--vnet-subnet-id", ctx.state["renderer_subnet_id"], "--name", "renderers", timeout=1800)
    ctx.state.save(network_connection="renderers", network_connection_s=elapsed(started))


def build_sandbox_disk(ctx, key, name, build_context):
    """Build and locate one disk; the scenario decides where its ID and measurements belong."""
    aca = ctx.aca(key)
    work = Path(tempfile.mkdtemp(prefix="atli-disk-context-", dir=ctx.work))
    try:
        context = build_context(work)
        started = time.monotonic()
        for attempt in range(6):
            result = aca("sandboxgroup", "disk", "create", "--source", str(context), "--name", name,
                         "--wait-timeout", "3000", check=False, timeout=3600)
            if result.returncode == 0:
                break
            if "403" not in result.stderr or attempt == 5:
                raise RuntimeError(ctx.runtime.redact(f"Disk image {name} failed:\n{result.stderr[-1500:]}"))
            time.sleep(30)
        seconds = elapsed(started)
    finally:
        shutil.rmtree(work, ignore_errors=True)
    disks = json.loads(aca("sandboxgroup", "disk", "list", "-o", "json").stdout)
    disk = next(d for d in disks if d.get("name") == name)
    return disk["id"], seconds


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
