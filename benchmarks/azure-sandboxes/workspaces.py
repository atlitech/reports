#!/usr/bin/env python3
"""Applications with many tenants: the hosted renderer service on Azure Container Apps Sandboxes,
with the provisioning service creating each workspace's renderer on its first conversion.

Everything runs in a resource group of its own, rg-atli-reports-workspaces-<UTC stamp> (and the
Container Apps environment's infrastructure group, <that>-cae), tagged
purpose=atli-reports-workspaces, plus one custom role definition whose only assignable scope is
that group. `cleanup` deletes all of it and checks.

  setup       The renderer network (no DNS: a DNS server address nothing holds, and a network
              security group denying the AzurePlatformDNS service tag); the gateway's network (a
              subnet with a NAT gateway and a static public address); the renderer sandbox group,
              connected to the renderer network, with disk images a and b built from this commit's
              server Dockerfile; a client sandbox group; a Key Vault (RBAC, no purge protection) for
              records; a Basic registry with the server and provisioner images (`az acr build`); two
              user-assigned identities: the provisioning service's (Data Owner on the renderer
              group, Key Vault Secrets Officer, AcrPull) and the gateway's (AcrPull, Key Vault
              Secrets User, a custom resume-only role on the renderer group); a workload-profiles
              Container Apps environment on the gateway subnet; the provisioning service
              (`serve`, internal ingress only, one replica) and the gateway (external ingress, two
              callers with tenant prefixes, Provisioning:Mode=OnDemand) as Container Apps; the
              client sandbox; and a smoke test (app-b creates and deletes appb-smoke).
  isolation   Scenario 1: refusals across callers, outside the prefixes, and of invalid IDs; the
              service's internal address from the laptop and from the client.
  leftover    Scenario 11: `start` creates a sandbox labeled as an appa- renderer without a record,
              as the provisioner labels them; `check` waits for the service to delete it.
  burst       Scenario 2: app-a's first conversions of 40 new workspaces at once.
  same        Scenario 3: 10 simultaneous first conversions of one new workspace.
  quota       Scenario 4: app-b makes 6 workspaces; then deletes 4 of them for scenario 5.
  fairness    Scenario 5: app-a names new workspaces as fast as it can for 60 s while app-b and
              app-a's existing workspaces convert.
  trim        Deletes the workspaces the flood made, through the gateway, as app-a.
  steady      Scenario 6: 30 workspaces, an invoice every 5 s each (every 20th the 49-page report),
              10 minutes; and 5 more converting every 180 s, so each request wakes its renderer.
  delete      Scenario 7: app-a deletes 5 workspaces; one is converted again.
  killswitch  Scenario 8: the operator disables a workspace; app-a's delete and conversions; enable.
  retire      Scenario 9: the service retires renderers stopped for 5 minutes, checked every minute.
  rollout     Scenario 10: `rollout --disk-image <b> --stopped retire`, run as a Container Apps job
              in the environment (so it reaches the renderers' ports from the NAT gateway's
              address), under light load.
  census      Records and renderer sandboxes by prefix, as JSON.
  verify      Searches the logs, the results, and the given files for every secret, address, and ID.
  cleanup     Deletes everything above and checks that it is gone.

Added during the run, for what it found:

  rebuild            A new build round of the server image and disks a and b (round 1's build
                     context was extracted under umask 077; contexts now get normalize_modes).
  listing            The raw GET sandboxes answer: 25 of the group's sandboxes on 2026-02-01-preview.
  listbug            The provisioner at f03e90e with more than 25 sandboxes: `list`, `disable` of a
                     tenant off the first page, and deletes through the gateway (`listbug delete`
                     for an older tenant); `listbug verify` repeats them after the fix.
  provisioner-image  Builds the provisioner image from HEAD and rolls the service onto it.
  debug              An app's revisions and latest console and system log lines, redacted.
  annotate           Merges a JSON file into the results (values recorded by hand, with a note).

Secrets (keys, verifiers, renderer credentials, tokens), addresses, FQDNs, and IDs stay in --work
(mode 0700) in files of mode 0600, and every log line and result is redacted as it is written.

Requirements: az (logged in to the subscription named Atli), aca (https://aka.ms/aca-cli-install;
--aca), the .NET SDK, git, openssl, and curl.

  benchmarks/azure-sandboxes/workspaces.py <phase> --work DIR --output results.json [--aca PATH]
"""
import argparse
import collections
import concurrent.futures
import json
import os
import re
import shutil
import socket
import ssl
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import production as prod  # noqa: E402  production.py's helpers: Context, Rest, Aca, arm, redaction

spike = prod.spike
from production import az, az_json, elapsed, log, run, sensitive, utc  # noqa: E402

REPO = prod.REPO
PURPOSE = "atli-reports-workspaces"
PREFIX = "rg-atli-reports-workspaces-"
SUBSCRIPTION_NAME = "Atli"
GATEWAY, SERVICE, JOB = "ca-gateway", "ca-provisioner", "job-provisioner"
CALLERS = {"app-a": "appa-", "app-b": "appb-"}
# Prefix: (MaxTenants, MaxCreatesPerMinute).
SERVICE_PREFIXES = {"appa-": (60, 20), "appb-": (5, 5)}
SERVICE_MAX_CREATES = 40
BIN = REPO / "artifacts/bin"
KV_API = "7.4"
SERVICE_DEFAULTS = {"retire_after_idle": "00:00:00", "retire_check": "00:01:00", "disk": "a"}
LEFTOVER = "appa-leftover"


# ---------------------------------------------------------------------------------------------
# Redaction: production.py's, plus port URLs and public IPv4 addresses
# ---------------------------------------------------------------------------------------------

_base_redact = prod.redact
_PORT_URL = re.compile(r"https?://[A-Za-z0-9.-]+\.adcproxy\.io[^\s\"',)]*")
_IPV4 = re.compile(r"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d.])")


def _private(match):
    a, b = int(match.group(1)), int(match.group(2))
    text = match.group(0)
    return (a in (10, 127, 0) or (a == 172 and 16 <= b <= 31) or (a == 192 and b == 168)
            or (a == 169 and b == 254) or text == "168.63.129.16" or a > 255)


def redact(text):
    text = _base_redact(text)
    text = _PORT_URL.sub("<port URL>", text)
    return _IPV4.sub(lambda m: m.group(0) if _private(m) else "<ip>", text)


prod.redact = redact  # production.py's log, run, and record use it from now on


# ---------------------------------------------------------------------------------------------
# Context
# ---------------------------------------------------------------------------------------------

class Ctx(prod.Context):
    def __init__(self, options):
        super().__init__(options)
        account = az_json("account", "show")
        if account["name"] != SUBSCRIPTION_NAME or account["id"] != self.subscription:
            raise RuntimeError(f"The Azure CLI's subscription is not {SUBSCRIPTION_NAME}, or not the run's.")
        prod.CONTEXT_WORK[0] = str(self.work)
        self.results.setdefault("notes", [])

    def note(self, text):
        if text not in self.results["notes"]:
            self.results["notes"].append(text)
            self.record("notes", self.results["notes"])

    @property
    def client(self):
        return self.state["client_sandbox"]

    def gateway_url(self):
        return f"https://{self.secret['gateway_fqdn']}"

    def disk(self, name):
        return self.secret[f"disk_{name}"]

    def disk_name(self, disk_id):
        for name in ["a", "b"]:
            if self.secret.get(f"disk_{name}") == disk_id:
                return name
        return "other" if disk_id else None


def tags():
    return ["--tags", f"purpose={PURPOSE}"]


def app_path(ctx, name):
    return f"{ctx.rg_id()}/providers/Microsoft.App/containerApps/{name}"


def job_path(ctx):
    return f"{ctx.rg_id()}/providers/Microsoft.App/jobs/{JOB}"


def percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    import math
    return round(ordered[max(0, math.ceil(fraction * len(ordered)) - 1)], 3)


def stats(values):
    values = [v for v in values if v is not None]
    if not values:
        return {"n": 0}
    return {"n": len(values), "min": round(min(values), 3), "p50": percentile(values, 0.5),
            "p95": percentile(values, 0.95), "p99": percentile(values, 0.99), "max": round(max(values), 3)}


# ---------------------------------------------------------------------------------------------
# Key Vault over REST, with a cached token
# ---------------------------------------------------------------------------------------------

_tokens = {}
_tokens_lock = threading.Lock()


def access_token(resource, name):
    """A token for `resource`, reused until 5 minutes before it expires. The Azure CLI hands out
    its own cached token, which may have little time left, so its expiry decides, not a fixed age."""
    with _tokens_lock:
        cached = _tokens.get(resource)
        if cached and time.time() < cached[1] - 300:
            return cached[0]
    view = az_json("account", "get-access-token", "--resource", resource)
    token, expires = view["accessToken"], float(view.get("expires_on") or time.time() + 600)
    sensitive(name, token)
    with _tokens_lock:
        _tokens[resource] = (token, expires)
    return token


def vault_token():
    return access_token("https://vault.azure.net", "vault token")


# production.py's Rest and listings use it: its own cache assumed 30 minutes.
prod.data_plane_token = lambda: access_token("https://dynamicsessions.io", "data-plane token")


def vault_request(url):
    request = urllib.request.Request(url, headers={"Authorization": f"Bearer {vault_token()}"})
    for attempt in range(5):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.status, json.loads(response.read() or b"{}")
        except urllib.error.HTTPError as error:
            if error.code == 429 or error.code >= 500:
                time.sleep(2 ** attempt)
                continue
            return error.code, {}
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            time.sleep(2 ** attempt)
    return 0, {}


def vault_secret_names(ctx):
    """(name, enabled) of every secret in the vault."""
    url = f"{ctx.state['vault_uri'].rstrip('/')}/secrets?api-version={KV_API}&maxresults=25"
    names = []
    while url:
        status, body = vault_request(url)
        if status != 200:
            raise RuntimeError(f"Listing the vault's secrets answered {status}")
        for item in body.get("value", []):
            names.append((item["id"].rsplit("/", 1)[-1], item.get("attributes", {}).get("enabled", True)))
        url = body.get("nextLink")
    return names


def read_record(ctx, tenant):
    """The tenant's record without its credential (registered as sensitive), or None."""
    status, body = vault_request(f"{ctx.state['vault_uri'].rstrip('/')}/secrets/renderer-{tenant}?api-version={KV_API}")
    if status != 200:
        return None
    record = json.loads(body["value"])
    key = record.pop("apiKey", None)
    if key:
        sensitive(f"renderer key {tenant} {str(record.get('sandboxId', ''))[:8]}", key)
        with prod._lock:
            keys = list(ctx.secret.get("renderer_keys", []))
        if key not in keys:
            ctx.secret.save(renderer_keys=keys + [key])
    record["disk"] = ctx.disk_name(record.pop("diskImageId", None))
    record.pop("url", None)
    return record


def read_records(ctx, match=None):
    tenants = [name[len("renderer-"):] for name, enabled in vault_secret_names(ctx)
               if enabled and name.startswith("renderer-") and (match is None or match(name[len("renderer-"):]))]
    with concurrent.futures.ThreadPoolExecutor(8) as pool:
        records = dict(zip(tenants, pool.map(lambda t: read_record(ctx, t), tenants)))
    return {t: r for t, r in records.items() if r}


# ---------------------------------------------------------------------------------------------
# The renderer group, as the data plane and the record store see it
# ---------------------------------------------------------------------------------------------

PAGED_API_VERSION = "2026-09-01-preview"


def _get_json(url):
    request = urllib.request.Request(url, headers={"Authorization": f"Bearer {prod.data_plane_token()}"})
    for attempt in range(5):
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return json.loads(response.read())
        except urllib.error.HTTPError as error:
            if error.code not in (429, 502, 503, 504):
                raise RuntimeError(f"Listing the sandboxes answered {error.code}") from None
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            pass
        time.sleep(2 ** attempt)
    raise RuntimeError("Listing the sandboxes failed")


def list_sandboxes(ctx, key="rend"):
    """Every sandbox of the group. api-version 2026-02-01-preview answers GET sandboxes with at most
    25 (pageSize, at most 100) and no continuation; 2026-09-01-preview pages with nextLink."""
    base = ctx.rest(key).base
    url, items, pages = f"{base}/sandboxes?api-version={PAGED_API_VERSION}&pageSize=100", [], 0
    while url:
        if not url.startswith(f"{base}/sandboxes?") or pages > 1000:
            raise RuntimeError("Unexpected nextLink in the sandbox listing")
        page = _get_json(url)
        items += page["value"]
        url, pages = page.get("nextLink"), pages + 1
    return list({s["id"]: s for s in items}.values())


def list_sandboxes_unpaged(ctx, key="rend"):
    """GET sandboxes as the hosting library's SandboxesClient sends it at f03e90e."""
    return _get_json(f"{ctx.rest(key).base}/sandboxes?api-version={prod.API_VERSION}")


def renderer_tenant(sandbox):
    labels = sandbox.get("labels") or {}
    if labels.get("app") == "atli-reports" and labels.get("role") == "renderer":
        return labels.get("tenant")
    return None


def census(ctx, match=None, with_states=True):
    """Every renderer sandbox and record, by tenant: counts per prefix, duplicates, leftovers. The
    leftover of scenario 11 is left out unless `match` asks for it."""
    if match is None:
        match = lambda tenant: tenant != LEFTOVER  # noqa: E731
    sandboxes = list_sandboxes(ctx)
    records = read_records(ctx, match)
    by_tenant = collections.defaultdict(list)
    for sandbox in sandboxes:
        tenant = renderer_tenant(sandbox)
        if tenant and (match is None or match(tenant)):
            by_tenant[tenant].append(sandbox)
    tenants = {}
    for tenant in sorted(set(by_tenant) | set(records)):
        record = records.get(tenant)
        boxes = by_tenant.get(tenant, [])
        tenants[tenant] = {
            "record_sandbox": record and record.get("sandboxId"),
            "record_disk": record and record.get("disk"),
            "record_created": record and record.get("createdAt"),
            "sandboxes": [{"id": s["id"], "state": s.get("state"),
                           "stopped_reason": (s.get("stateDetails") or {}).get("stoppedReason"),
                           "stopped_at": (s.get("stateDetails") or {}).get("stoppedAt"),
                           "created_at": s.get("createdAt")} if with_states else s["id"] for s in boxes],
        }
    named = {t: v["record_sandbox"] for t, v in tenants.items()}
    summary = {}
    for prefix in list(SERVICE_PREFIXES) + ["other"]:
        mine = [t for t in tenants if (t.startswith(prefix) if prefix != "other"
                                       else not any(t.startswith(p) for p in SERVICE_PREFIXES))]
        summary[prefix] = {
            "records": sum(1 for t in mine if tenants[t]["record_sandbox"]),
            "sandboxes": sum(len(tenants[t]["sandboxes"]) for t in mine),
            "states": dict(collections.Counter(s["state"] if with_states else "?" for t in mine
                                               for s in tenants[t]["sandboxes"])),
        }
    ids = lambda t: [s["id"] if with_states else s for s in tenants[t]["sandboxes"]]  # noqa: E731
    return {
        "at_utc": utc(), "by_prefix": summary,
        "duplicates": {t: len(v["sandboxes"]) for t, v in tenants.items() if len(v["sandboxes"]) > 1},
        "leftovers": {t: [i for i in ids(t) if i != named[t]] for t in tenants
                      if any(i != named[t] for i in ids(t))},
        "records_without_sandbox": [t for t, v in tenants.items()
                                    if v["record_sandbox"] and v["record_sandbox"] not in ids(t)],
        "tenants": tenants,
    }


def census_brief(c, prefixes=None):
    return {"at_utc": c["at_utc"], "by_prefix": c["by_prefix"], "duplicates": c["duplicates"],
            "leftovers": c["leftovers"], "records_without_sandbox": c["records_without_sandbox"]}


# ---------------------------------------------------------------------------------------------
# setup
# ---------------------------------------------------------------------------------------------

def setup(ctx):
    commit = run(["git", "-C", str(REPO), "rev-parse", "--short", "HEAD"]).stdout.strip()
    if "resource_group" not in ctx.state:
        stamp = time.strftime("%Y%m%d%H%M%S", time.gmtime())
        rg = PREFIX + stamp
        log(f"Resource group {rg} in {ctx.region}")
        az("group", "create", "--name", rg, "--location", ctx.region, *tags(), f"commit={commit}", "-o", "none")
        ctx.state.save(resource_group=rg, stamp=stamp, commit=commit, started_utc=utc(), infra_rg=f"{rg}-cae",
                       group_rend=f"reports-ws-rend-{stamp}", group_client=f"reports-ws-client-{stamp}",
                       vault=f"kv-atliws-{stamp}", registry=f"acratliws{stamp}",
                       workspace=f"log-atli-ws-{stamp}", identity_gateway=f"id-atli-ws-gateway-{stamp}",
                       identity_service=f"id-atli-ws-provisioner-{stamp}",
                       environment=f"cae-atli-ws-{stamp}",
                       role_name=f"Atli Reports Sandbox Resumer (workspaces {stamp})",
                       service_settings=dict(SERVICE_DEFAULTS))
    if ctx.state["resource_group"] != PREFIX + ctx.state["stamp"]:
        raise RuntimeError("The state names a resource group that is not this run's.")
    prod.check_laptop_ip(ctx)
    me = az("ad", "signed-in-user", "show", "--query", "id", "-o", "tsv").stdout.strip()
    ctx.secret.save(my_object_id=me)
    sensitive("my object id", me)
    timings = ctx.results.get("setup", {}).get("timings_s", {})

    def timed(name, function):
        if name in timings:
            function()  # idempotent: finishes what an interrupted run left
            return
        started = time.monotonic()
        function()
        timings[name] = elapsed(started)
        log(f"{name}: {timings[name]} s")

    with concurrent.futures.ThreadPoolExecutor(8) as pool:
        networks = pool.submit(timed, "networks", lambda: setup_networks(ctx))
        vault = pool.submit(timed, "vault", lambda: setup_vault(ctx))
        identities = pool.submit(timed, "identities_and_workspace", lambda: setup_identities(ctx))
        images = pool.submit(timed, "registry_and_images", lambda: setup_registry(ctx, commit))
        groups = pool.submit(timed, "sandbox_groups_and_disks", lambda: setup_sandbox_groups(ctx, networks, commit))
        for future in [networks, identities]:
            future.result()
        environment = pool.submit(timed, "container_apps_environment", lambda: setup_environment(ctx))
        vault.result()
        # The roles need the registry and the groups to exist, not the images or disks.
        while not (ctx.state.get("registry_id") and ctx.state.get("propagation_rend")):
            if images.done() and images.exception():
                images.result()
            if groups.done() and groups.exception():
                groups.result()
            time.sleep(5)
        roles = pool.submit(timed, "role_assignments", lambda: setup_roles(ctx))
        for future in [images, groups, roles, environment]:
            future.result()
    timed("service", lambda: deploy_service(ctx))
    timed("gateway", lambda: deploy_gateway(ctx))
    timed("client_sandbox", lambda: setup_client(ctx))
    smoke = ctx.results.get("setup", {}).get("smoke") or smoke_test(ctx)
    ctx.record("setup", {
        "commit": ctx.state["commit"], "location": ctx.region, "started_utc": ctx.state["started_utc"],
        "resource_group": ctx.rg, "aca_cli": run(["aca", "--version"]).stdout.strip(),
        "az_cli": az("version", "--query", '"azure-cli"', "-o", "tsv").stdout.strip(),
        "timings_s": timings,
        "disk_build_s": {k: ctx.state.get(f"disk_{k}_build_s") for k in ["a", "b"]},
        "acr_build_s": {k: ctx.state.get(f"acr_{k}_build_s") for k in ["server", "provisioner"]},
        "data_plane_propagation_s": {k: ctx.state.get(f"propagation_{k}") for k in ["rend", "client"]},
        "deploys": ctx.state.get("deploys", []),
        "client": ctx.state.get("client_view"), "smoke": smoke,
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
    while vault_request(f"{ctx.state['vault_uri'].rstrip('/')}/secrets?api-version={KV_API}&maxresults=1")[0] != 200:
        if time.monotonic() - started > 900:
            raise RuntimeError("Key Vault Secrets Officer did not take effect within 15 minutes.")
        time.sleep(10)


def setup_identities(ctx):
    rg = ctx.rg
    for key in ["gateway", "service"]:
        if f"identity_{key}_id" not in ctx.state:
            log(f"Managed identity {ctx.state[f'identity_{key}']}")
            view = az_json("identity", "create", "-g", rg, "-n", ctx.state[f"identity_{key}"], "-l", ctx.region, *tags())
            ctx.state.save(**{f"identity_{key}_id": view["id"]})
            ctx.secret.save(**{f"identity_{key}_client_id": view["clientId"],
                               f"identity_{key}_principal_id": view["principalId"]})
        sensitive(f"{key} identity client id", ctx.secret[f"identity_{key}_client_id"])
        sensitive(f"{key} identity principal id", ctx.secret[f"identity_{key}_principal_id"])
    if "workspace_id" not in ctx.state:
        log(f"Log Analytics workspace {ctx.state['workspace']}")
        view = az_json("monitor", "log-analytics", "workspace", "create", "-g", rg, "-n", ctx.state["workspace"],
                       "-l", ctx.region, *tags())
        ctx.state.save(workspace_id=view["id"])
        ctx.secret.save(workspace_customer_id=view["customerId"])
    sensitive("workspace customer id", ctx.secret["workspace_customer_id"])


def normalize_modes(context):
    """Gives the build context the modes a checkout under umask 022 has (0644, 0755 for
    executables and directories). The server image copies appsettings.json with its mode from the
    context, so a context extracted under umask 077 makes an image whose server cannot read it."""
    for root, dirs, files in os.walk(context):
        for name in dirs:
            os.chmod(os.path.join(root, name), 0o755)
        for name in files:
            path = os.path.join(root, name)
            os.chmod(path, 0o755 if os.stat(path).st_mode & 0o100 else 0o644)
    os.chmod(context, 0o755)
    return context


def server_context(work):
    return normalize_modes(spike.build_context(work))


def build_suffix(ctx):
    """Images and disks of a later build round carry its number."""
    round_ = ctx.state.get("build_round", 1)
    return "" if round_ == 1 else f"-r{round_}"


def provisioner_context(work):
    """A git archive of what the provisioner's Dockerfile copies, at HEAD."""
    context = work / "context"
    context.mkdir()
    paths = ["global.json", "Directory.Build.props", "Directory.Packages.props", ".editorconfig",
             "src/Atli.Reports.Hosting", "src/Atli.Reports.Provisioner"]
    import subprocess
    archive = subprocess.run(["git", "-C", str(REPO), "archive", "HEAD", *paths], capture_output=True,
                             check=True).stdout
    subprocess.run(["tar", "-x", "-C", str(context)], input=archive, check=True)
    return normalize_modes(context)


def setup_registry(ctx, commit):
    rg, registry = ctx.rg, ctx.state["registry"]
    if "registry_id" not in ctx.state:
        log(f"Container registry {registry} (Basic)")
        view = az_json("acr", "create", "-g", rg, "-n", registry, "-l", ctx.region, *tags(), "--sku", "Basic",
                       "--admin-enabled", "false")
        ctx.state.save(registry_id=view["id"])
        ctx.secret.save(registry_server=view["loginServer"])
    sensitive("registry server", ctx.secret["registry_server"])

    def build(kind):
        if f"image_{kind}" in ctx.state:
            return
        work = Path(tempfile.mkdtemp(prefix=f"atli-ws-acr-{kind}-", dir=ctx.work))
        try:
            if kind == "server":
                context = server_context(work)
                dockerfile = context / "Dockerfile"
                image = f"atli-reports-server:{commit}{build_suffix(ctx)}"
            else:
                context = provisioner_context(work)
                dockerfile = context / "src/Atli.Reports.Provisioner/Dockerfile"
                image = f"atli-reports-provisioner:{commit}{build_suffix(ctx)}"
            log(f"az acr build {image}")
            started = time.monotonic()
            result = az("acr", "build", "--registry", registry, "--image", image, "--file", str(dockerfile),
                        "--platform", "linux/amd64", str(context), check=False, timeout=3600)
            (ctx.work / f"logs/acr-build-{kind}.log").write_text(redact(result.stdout[-20000:] + result.stderr[-5000:]))
            if result.returncode != 0:
                raise RuntimeError(f"az acr build {kind} failed; see logs/acr-build-{kind}.log")
            ctx.state.save(**{f"image_{kind}": image, f"acr_{kind}_build_s": elapsed(started)})
        finally:
            shutil.rmtree(work, ignore_errors=True)

    with concurrent.futures.ThreadPoolExecutor(2) as pool:
        list(pool.map(build, ["server", "provisioner"]))


def image(ctx, kind):
    return f"{ctx.secret['registry_server']}/{ctx.state[f'image_{kind}']}"


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

    with concurrent.futures.ThreadPoolExecutor(2) as pool:
        list(pool.map(create, ["rend", "client"]))
    networks.result()
    if "network_connection" not in ctx.state:
        log("Connecting the renderer group to the renderer network (connection 'renderers')")
        started = time.monotonic()
        ctx.aca("rend")("sandboxgroup", "network", "create", "--group", ctx.group("rend"),
                        "--vnet-subnet-id", ctx.state["renderer_subnet_id"], "--name", "renderers", timeout=1800)
        ctx.state.save(network_connection="renderers", network_connection_s=elapsed(started))

    def build(suffix):
        name = f"reports-server-{commit}{build_suffix(ctx)}-{suffix}"
        if f"disk_{suffix}" in ctx.secret:
            return
        aca = ctx.aca("rend")
        work = Path(tempfile.mkdtemp(prefix="atli-ws-context-", dir=ctx.work))
        try:
            context = server_context(work)
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
        ctx.secret.save(**{f"disk_{suffix}": disk["id"]})
        sensitive(f"disk {suffix}", disk["id"])
        ctx.state.save(**{f"disk_{suffix}_name": name, f"disk_{suffix}_build_s": seconds})
        log(f"Disk {name}: {seconds} s")

    with concurrent.futures.ThreadPoolExecutor(2) as pool:
        list(pool.map(build, ["a", "b"]))


def assign(ctx, name, principal, role, scope):
    assignments = ctx.state.get("assignments", {})
    if name in assignments:
        return
    for attempt in range(40):  # a new identity or role definition takes a while to be assignable
        result = az("role", "assignment", "create", "--assignee-object-id", principal,
                    "--assignee-principal-type", "ServicePrincipal", "--role", role, "--scope", scope,
                    "--query", "id", "-o", "tsv", check=False)
        if result.returncode == 0:
            break
        time.sleep(15)
    else:
        raise RuntimeError(f"Role assignment {name} failed: {redact(result.stderr[-500:])}")
    with prod._lock:
        assignments = dict(ctx.state.get("assignments", {}))
    assignments[name] = result.stdout.strip()
    ctx.state.save(assignments=assignments, **{f"assigned_{name}_utc": utc()})


def setup_roles(ctx):
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
    gateway, service = ctx.secret["identity_gateway_principal_id"], ctx.secret["identity_service_principal_id"]
    group = ctx.group_id("rend")
    plan = [
        ("service_data_owner", service, "Container Apps SandboxGroup Data Owner", group),
        ("service_vault_officer", service, "Key Vault Secrets Officer", ctx.state["vault_id"]),
        ("service_acr_pull", service, "AcrPull", ctx.state["registry_id"]),
        ("gateway_acr_pull", gateway, "AcrPull", ctx.state["registry_id"]),
        ("gateway_vault_user", gateway, "Key Vault Secrets User", ctx.state["vault_id"]),
        ("gateway_resumer", gateway, ctx.state["role_definition_name"], group),
    ]
    with concurrent.futures.ThreadPoolExecutor(3) as pool:
        list(pool.map(lambda item: assign(ctx, *item), plan))
    log("Service identity: Data Owner, Key Vault Secrets Officer, AcrPull. Gateway identity: AcrPull, "
        "Key Vault Secrets User, the resume-only role.")


def setup_environment(ctx):
    path = f"{ctx.rg_id()}/providers/Microsoft.App/managedEnvironments/{ctx.state['environment']}"
    ctx.state.save(environment_id=path)
    view = prod.arm("GET", path, check=False)
    if not view:
        log(f"Container Apps environment {ctx.state['environment']} (workload profiles, gateway subnet)")
        key = az("monitor", "log-analytics", "workspace", "get-shared-keys", "-g", ctx.rg, "-n",
                 ctx.state["workspace"], "--query", "primarySharedKey", "-o", "tsv").stdout.strip()
        sensitive("workspace key", key)
        prod.arm("PUT", path, {
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
        view = prod.arm("GET", path)
        state = view["properties"].get("provisioningState")
        if state == "Succeeded":
            break
        if state in ("Failed", "Canceled") or time.monotonic() - started > 2400:
            raise RuntimeError(f"Environment {state}")
        time.sleep(15)
    ctx.secret.save(environment_domain=view["properties"].get("defaultDomain"),
                    environment_ip=view["properties"].get("staticIp"))
    sensitive("environment domain", ctx.secret["environment_domain"])
    sensitive("environment ip", ctx.secret["environment_ip"])
    if az("group", "exists", "-n", ctx.state["infra_rg"]).stdout.strip() == "true":
        tagged = az("group", "update", "-n", ctx.state["infra_rg"], "--set", f"tags.purpose={PURPOSE}", "-o", "none",
                    check=False)
        ctx.state.save(infra_rg_tagged=tagged.returncode == 0)


# ---------------------------------------------------------------------------------------------
# Keys, the provisioning service, and the gateway
# ---------------------------------------------------------------------------------------------

def credentials(ctx, name):
    """A key from scripts/create-reports-api-key.sh: (id, verifier, key)."""
    directory = ctx.work / f"key-{name}"
    if not directory.exists():
        run(["bash", str(REPO / "scripts/create-reports-api-key.sh"), str(directory), name])
    server = dict(line.split("=", 1) for line in (directory / "server.env").read_text().split())
    key = re.search(r"^ReportsClient__ApiKey=(.+)$", (directory / "client.env").read_text(), re.M).group(1)
    key_id = server["ReportsServer__Authentication__ApiKeys__0__Id"]
    verifier = server["ReportsServer__Authentication__ApiKeys__0__Hash"]
    sensitive(f"{name} key", key)
    sensitive(f"{name} key verifier", verifier)
    return key_id, verifier, key


def logging_env():
    return {
        "Logging__Console__FormatterName": "simple",
        "Logging__Console__FormatterOptions__SingleLine": "true",
        "Logging__Console__FormatterOptions__UseUtcTimestamp": "true",
        "Logging__Console__FormatterOptions__TimestampFormat": "HH:mm:ss.fff ",
    }


def provisioner_env(ctx, disk, with_service=True, retire_after_idle="00:00:00", retire_check="00:01:00"):
    """The provisioner's settings in the environment, as the service and the job run it."""
    env = {
        "Provisioner__Sandboxes__SubscriptionId": ctx.subscription,
        "Provisioner__Sandboxes__ResourceGroup": ctx.rg,
        "Provisioner__Sandboxes__SandboxGroup": ctx.group("rend"),
        "Provisioner__Sandboxes__Region": ctx.region,
        "Provisioner__Sandboxes__ManagedIdentityClientId": ctx.secret["identity_service_client_id"],
        "Provisioner__Records__Store": "KeyVault",
        "Provisioner__Records__VaultUri": ctx.state["vault_uri"],
        "Provisioner__Records__ManagedIdentityClientId": ctx.secret["identity_service_client_id"],
        "Provisioner__DiskImageId": ctx.disk(disk),
        "Provisioner__Size": "S",
        "Provisioner__NetworkConnection": "renderers",
        "Provisioner__AllowedSourceCidrs__0": f"{ctx.secret['nat_ip']}/32",
        "Provisioner__AutoSuspendAfter": "00:01:00",
        "Provisioner__Service__MaxCreatesPerMinute": str(SERVICE_MAX_CREATES),
        "Provisioner__Service__RetireAfterIdle": retire_after_idle,
        "Provisioner__Service__RetireCheckInterval": retire_check,
        **logging_env(),
    }
    for index, (prefix, (max_tenants, per_minute)) in enumerate(SERVICE_PREFIXES.items()):
        env[f"Provisioner__Service__TenantPrefixes__{index}__Prefix"] = prefix
        env[f"Provisioner__Service__TenantPrefixes__{index}__MaxTenants"] = str(max_tenants)
        env[f"Provisioner__Service__TenantPrefixes__{index}__MaxCreatesPerMinute"] = str(per_minute)
    return env


def wait_app(ctx, path, started, limit=2400):
    while True:
        view = prod.arm("GET", path)
        properties = view["properties"]
        if (properties.get("provisioningState") == "Succeeded"
                and properties.get("latestReadyRevisionName") == properties.get("latestRevisionName")):
            return properties
        if properties.get("provisioningState") == "Failed" or time.monotonic() - started > limit:
            raise RuntimeError(f"{path.rsplit('/', 1)[-1]}: {properties.get('provisioningState')}, "
                               f"latest ready revision {properties.get('latestReadyRevisionName')}")
        time.sleep(5)


def deploy_service(ctx, **changes):
    """Creates or updates the provisioning service; returns once its latest revision is ready."""
    settings = dict(ctx.state.get("service_settings") or SERVICE_DEFAULTS)
    settings.update(changes)
    deployed = dict(settings, image=image(ctx, "provisioner"), disk_build=ctx.state.get(f"disk_{settings['disk']}_name"))
    if ctx.state.get("service_deployed") == deployed and ctx.secret.get("service_fqdn"):
        return
    key_id, verifier, _ = credentials(ctx, "gateway")
    env = provisioner_env(ctx, settings["disk"], retire_after_idle=settings["retire_after_idle"],
                          retire_check=settings["retire_check"])
    env_list = [{"name": n, "value": v} for n, v in env.items()]
    env_list += [{"name": "Provisioner__Service__ApiKeys__0__Id", "secretRef": "gateway-key-id"},
                 {"name": "Provisioner__Service__ApiKeys__0__Hash", "secretRef": "gateway-key-verifier"}]
    identity = ctx.state["identity_service_id"]
    body = {
        "location": ctx.region, "tags": {"purpose": PURPOSE},
        "identity": {"type": "UserAssigned", "userAssignedIdentities": {identity: {}}},
        "properties": {
            "environmentId": ctx.state["environment_id"], "workloadProfileName": "Consumption",
            "configuration": {
                "activeRevisionsMode": "Single",
                "ingress": {"external": False, "targetPort": 8080, "transport": "auto", "allowInsecure": False},
                "registries": [{"server": ctx.secret["registry_server"], "identity": identity}],
                "secrets": [{"name": "gateway-key-id", "value": key_id},
                            {"name": "gateway-key-verifier", "value": verifier}],
            },
            "template": {
                "terminationGracePeriodSeconds": 150,
                "containers": [{
                    "name": "provisioner", "image": image(ctx, "provisioner"),
                    "resources": {"cpu": 0.5, "memory": "1Gi"}, "env": env_list,
                    "probes": [
                        {"type": "Liveness", "httpGet": {"path": "/health/live", "port": 8080},
                         "initialDelaySeconds": 2, "periodSeconds": 10, "timeoutSeconds": 3, "failureThreshold": 3},
                        {"type": "Readiness", "httpGet": {"path": "/health/ready", "port": 8080},
                         "initialDelaySeconds": 2, "periodSeconds": 5, "timeoutSeconds": 5, "failureThreshold": 3},
                    ],
                }],
                "scale": {"minReplicas": 1, "maxReplicas": 1},
            },
        },
    }
    log(f"Deploying the provisioning service: {settings}")
    started = time.monotonic()
    since = utc()
    prod.arm("PUT", app_path(ctx, SERVICE), body)
    properties = wait_app(ctx, app_path(ctx, SERVICE), started)
    fqdn = properties["configuration"]["ingress"]["fqdn"]
    ctx.secret.save(service_fqdn=fqdn)
    sensitive("service host", fqdn)
    deploys = ctx.state.get("deploys", [])
    deploys.append({"app": SERVICE, "settings": settings, "revision": properties["latestRevisionName"],
                    "ready_after_s": elapsed(started), "since_utc": since, "at_utc": utc()})
    ctx.state.save(service_settings=settings, service_deployed=deployed, deploys=deploys,
                   service_revision=properties["latestRevisionName"])
    log(f"Provisioning service revision {properties['latestRevisionName']} ready after {elapsed(started)} s")


def deploy_gateway(ctx, log_requests=False):
    settings = {"log_requests": log_requests, "image": image(ctx, "server")}
    if ctx.state.get("gateway_deployed") == settings and ctx.secret.get("gateway_fqdn"):
        return
    keys = {caller: credentials(ctx, caller) for caller in CALLERS}
    _, _, service_key = credentials(ctx, "gateway")
    env = {
        "ReportsServer__Mode": "Gateway",
        "ReportsServer__Authentication__Mode": "ApiKey",
        "ReportsServer__Limits__MaxConcurrentRequestsPerCaller": "64",
        "ReportsServer__Gateway__Records__Store": "KeyVault",
        "ReportsServer__Gateway__Records__VaultUri": ctx.state["vault_uri"],
        "ReportsServer__Gateway__Records__ManagedIdentityClientId": ctx.secret["identity_gateway_client_id"],
        "ReportsServer__Gateway__Wake__Mode": "None",
        "ReportsServer__Gateway__Provisioning__Mode": "OnDemand",
        "ReportsServer__Gateway__Provisioning__Url": f"https://{ctx.secret['service_fqdn']}",
        **logging_env(),
    }
    secrets, refs = [{"name": "provisioning-key", "value": service_key}], [
        {"name": "ReportsServer__Gateway__Provisioning__ApiKey", "secretRef": "provisioning-key"}]
    for index, (caller, prefix) in enumerate(CALLERS.items()):
        key_id, verifier, _ = keys[caller]
        env[f"ReportsServer__Authentication__ApiKeys__{index}__CallerId"] = caller
        env[f"ReportsServer__Authentication__ApiKeys__{index}__Permissions__0"] = "reports.convert"
        env[f"ReportsServer__Authentication__ApiKeys__{index}__Permissions__1"] = "reports.tenants"
        env[f"ReportsServer__Gateway__Tenants__{index}__CallerId"] = caller
        env[f"ReportsServer__Gateway__Tenants__{index}__TenantPrefixes__0"] = prefix
        secrets += [{"name": f"{caller}-key-id", "value": key_id}, {"name": f"{caller}-key-verifier", "value": verifier}]
        refs += [{"name": f"ReportsServer__Authentication__ApiKeys__{index}__Id", "secretRef": f"{caller}-key-id"},
                 {"name": f"ReportsServer__Authentication__ApiKeys__{index}__Hash", "secretRef": f"{caller}-key-verifier"}]
    if log_requests:
        env["Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information"
    identity = ctx.state["identity_gateway_id"]
    body = {
        "location": ctx.region, "tags": {"purpose": PURPOSE},
        "identity": {"type": "UserAssigned", "userAssignedIdentities": {identity: {}}},
        "properties": {
            "environmentId": ctx.state["environment_id"], "workloadProfileName": "Consumption",
            "configuration": {
                "activeRevisionsMode": "Single",
                "ingress": {"external": True, "targetPort": 8080, "transport": "auto", "allowInsecure": False},
                "registries": [{"server": ctx.secret["registry_server"], "identity": identity}],
                "secrets": secrets,
            },
            "template": {
                "containers": [{
                    "name": "gateway", "image": image(ctx, "server"), "resources": {"cpu": 1.0, "memory": "2Gi"},
                    "env": [{"name": n, "value": v} for n, v in env.items()] + refs,
                    "probes": [
                        {"type": "Liveness", "httpGet": {"path": "/health/live", "port": 8080},
                         "initialDelaySeconds": 1, "periodSeconds": 10, "timeoutSeconds": 3, "failureThreshold": 3},
                        {"type": "Readiness", "httpGet": {"path": "/health/ready", "port": 8080},
                         "initialDelaySeconds": 1, "periodSeconds": 5, "timeoutSeconds": 10, "failureThreshold": 3},
                    ],
                }],
                "scale": {"minReplicas": 1, "maxReplicas": 1},
            },
        },
    }
    log(f"Deploying the gateway: {settings}")
    started = time.monotonic()
    since = utc()
    prod.arm("PUT", app_path(ctx, GATEWAY), body)
    properties = wait_app(ctx, app_path(ctx, GATEWAY), started)
    fqdn = properties["configuration"]["ingress"]["fqdn"]
    ctx.secret.save(gateway_fqdn=fqdn)
    sensitive("gateway host", fqdn)
    while prod.http_get(f"https://{fqdn}/health/ready")[0] != 200:
        if time.monotonic() - started > 1200:
            raise RuntimeError("The gateway did not answer /health/ready")
        time.sleep(2)
    deploys = ctx.state.get("deploys", [])
    deploys.append({"app": GATEWAY, "settings": settings, "revision": properties["latestRevisionName"],
                    "ready_after_s": elapsed(started), "since_utc": since, "at_utc": utc()})
    ctx.state.save(gateway_deployed=settings, deploys=deploys, gateway_revision=properties["latestRevisionName"])
    log(f"Gateway revision {properties['latestRevisionName']} ready after {elapsed(started)} s")


# ---------------------------------------------------------------------------------------------
# The client sandbox
# ---------------------------------------------------------------------------------------------

def setup_client(ctx):
    aca, rest = ctx.aca("client"), ctx.rest("client")
    if "client_sandbox" not in ctx.state:
        log("Client sandbox (python-3.12, egress only to the gateway's host and the service's internal host)")
        rules = ["--egress-rule", f"{ctx.secret['gateway_fqdn']}:Allow",
                 "--egress-rule", f"{ctx.secret['service_fqdn']}:Allow"]
        size = ("4000m", "8192Mi")
        result = aca("sandbox", "create", "--disk", "python-3.12", "--cpu", size[0], "--memory", size[1],
                     "--egress-default", "Deny", *rules, "--label", "role=workspaces-client", "-o", "json", check=False)
        if result.returncode != 0:
            size = ("2000m", "4096Mi")
            result = aca("sandbox", "create", "--disk", "python-3.12", "--cpu", size[0], "--memory", size[1],
                         "--egress-default", "Deny", *rules, "--label", "role=workspaces-client", "-o", "json")
        match = re.search(rf"Created sandbox: ({spike.UUID})", result.stdout)
        ctx.state.save(client_sandbox=match.group(1), client_size=list(size))
    client = ctx.client
    rest.request("POST", f"sandboxes/{client}/lifecycle", {"autoSuspendPolicy": {"enabled": False, "interval": 0}})
    rest.ensure_running(client)
    view = rest.sandbox(client) or {}
    outbound = view.get("outboundIpAddresses") or []
    ctx.secret.save(client_outbound_ips=outbound)
    for index, address in enumerate(outbound):
        sensitive(f"client outbound ip {index}", address)
    upload_client(ctx)
    info = aca.exec(client, "nproc; python3 --version; grep MemTotal /proc/meminfo").stdout.split()
    ctx.state.save(client_view={"size": ctx.state["client_size"], "info": info, "outbound_addresses": len(outbound)})


def upload_client(ctx):
    aca, client = ctx.aca("client"), ctx.client
    requests = ctx.work / "requests"
    if not requests.exists():
        requests.mkdir(mode=0o700)
        spike.write_requests(requests, ctx.work / "key-app-a")
        for caller in CALLERS:
            path = requests / f"{caller}.key"
            path.write_text(credentials(ctx, caller)[2])
            path.chmod(0o600)
        (requests / "api-key").unlink()
    aca.write(client, "/root/run/workspace-client.py", HERE / "workspace-client.py")
    for name in ["invoice.json", "long-table.json", "app-a.key", "app-b.key"]:
        aca.write(client, f"/root/run/{name}", requests / name)
    aca.exec(client, "chmod 600 /root/run/app-a.key /root/run/app-b.key")


def client_once(ctx, tenant, caller="app-a", body="invoice.json", method="POST", path="/convert"):
    command = (f"cd /root/run && python3 workspace-client.py once --url {ctx.gateway_url()} "
               f"--key-file {caller}.key --method {method} --path {path}"
               + (f" --tenant {tenant}" if tenant else "") + (f" --body {body}" if body else ""))
    for attempt in range(3):
        output = ctx.aca("client").exec(ctx.client, command, check=False).stdout
        line = next((l for l in output.splitlines() if l.startswith("{")), None)
        if line:
            result = json.loads(line)
            result.pop("at", None)
            return result
        time.sleep(3)
    return {"status": "no output", "output": redact(output[-300:])}


def client_delete(ctx, tenant, caller="app-a"):
    return client_once(ctx, None, caller, None, "DELETE", f"/tenants/{tenant}")


def client_start(ctx, label, streams):
    """Starts a streams run in the client in the background; returns the client's start epoch."""
    plan = {"url": ctx.gateway_url(), "bodies": {"invoice": "invoice.json", "long-table": "long-table.json"},
            "streams": streams}
    path = ctx.work / "client" / f"plan-{label}.json"
    path.write_text(json.dumps(plan))
    path.chmod(0o600)
    aca, client = ctx.aca("client"), ctx.client
    aca.write(client, f"/root/run/plan-{label}.json", path)
    aca.exec(client, f"cd /root/run && rm -rf out-{label} run-{label}.out && setsid nohup python3 "
                     f"workspace-client.py streams --plan plan-{label}.json --out out-{label} > run-{label}.out "
                     f"2>&1 < /dev/null &")
    started = time.monotonic()
    while time.monotonic() - started < 120:
        text = aca.exec(client, f"cat /root/run/out-{label}/started", check=False).stdout.strip()
        if re.fullmatch(r"[0-9.]+", text or ""):
            return float(text)
        time.sleep(2)
    raise RuntimeError(f"client run {label} did not start")


def client_collect(ctx, label, timeout):
    aca, client = ctx.aca("client"), ctx.client
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        text = aca.exec(client, f"cat /root/run/run-{label}.out", check=False).stdout
        if '"done": true' in text:
            break
        if "Traceback" in text:
            raise RuntimeError(f"client run {label} failed: {redact(text[-800:])}")
        time.sleep(10)
    else:
        raise RuntimeError(f"client run {label}: no result within {timeout} s")
    local = ctx.work / "client" / f"records-{label}.json"
    aca("sandbox", "fs", "cp", f"{client}:/root/run/out-{label}/records.json", str(local))
    local.chmod(0o600)
    return json.loads(local.read_text())


def client_streams(ctx, label, streams, timeout):
    since = utc()
    start_epoch = client_start(ctx, label, streams)
    data = client_collect(ctx, label, timeout)
    data["since_utc"], data["until_utc"] = since, utc()
    return data


def classify(record):
    """An answer's class, by status, kind, and the gateway's fixed messages."""
    status, kind = record.get("status"), record.get("kind") or record.get("error") or ""
    detail = (record.get("detail") or "").lower()
    if status == 200 and record.get("pdf"):
        return "pdf"
    if status == 204:
        return "204"
    if status == 503 and kind == "Busy":
        if "new tenants" in detail:
            return "503 Busy: new-tenant budget"
        if "provisioning service is at its rate limit" in detail:
            return "503 Busy: service rate limit"
        if "in-flight conversion limit" in detail:
            return "503 Busy: tenant limit"
        if "deletion limit" in detail:
            return "503 Busy: deletion limit"
        return "503 Busy: other"
    if status == 503 and "quota" in detail:
        return "503 BrowserUnavailable: quota full"
    text = f"{status} {kind}".strip()
    return f"{text}: {record.get('detail', '')[:90]}" if record.get("detail") else text


def by_class(records):
    return dict(collections.Counter(classify(r) for r in records).most_common())


# ---------------------------------------------------------------------------------------------
# Logs and metrics
# ---------------------------------------------------------------------------------------------

def logs(ctx, since_utc, until_utc=None, app=None, container=None, contains=None, limit=20000):
    """Console lines of an app (or a job's container) from Log Analytics; ingestion lags minutes."""
    where = (f"ContainerAppName_s == '{app}'" if app else f"ContainerName_s == '{container}'")
    query = (f"ContainerAppConsoleLogs_CL | where {where} "
             f"| where TimeGenerated >= datetime({since_utc})"
             + (f" and TimeGenerated <= datetime({until_utc})" if until_utc else "")
             + (" | where " + " or ".join(f"Log_s has '{c}'" for c in contains) if contains else "")
             + " | project TimeGenerated, ContainerGroupName_s, RevisionName_s, Log_s | order by TimeGenerated asc"
             + f" | take {limit}")
    rows = az_json("monitor", "log-analytics", "query", "-w", ctx.secret["workspace_customer_id"],
                   "--analytics-query", query, "-t", "P1D", check=False) or []
    return [{"time": row.get("TimeGenerated"), "replica": row.get("ContainerGroupName_s"),
             "line": redact(row.get("Log_s", ""))} for row in rows]


def log_events(ctx, app, since, until):
    """Counts of each logged event ([n]) by category, in an app's console log."""
    query = (f"ContainerAppConsoleLogs_CL | where ContainerAppName_s == '{app}' "
             f"| where TimeGenerated between (datetime({since}) .. datetime({until})) "
             "| extend event = extract(@'(\\w+(\\.\\w+)*)\\[(\\d+)\\]', 0, Log_s) | where isnotempty(event) "
             "| summarize count() by event")
    rows = az_json("monitor", "log-analytics", "query", "-w", ctx.secret["workspace_customer_id"],
                   "--analytics-query", query, "-t", "P1D", check=False) or []
    return dict(sorted(((r["event"], int(r["count_"])) for r in rows), key=lambda item: -item[1]))


def save_log(ctx, name, lines):
    path = ctx.work / "logs" / f"{name}.log"
    path.write_text("".join(f"{l['time']} {l['line']}\n" for l in lines))
    path.chmod(0o600)
    return str(path.name)


def wait_logs(ctx, since, until, app, predicate, contains=None, timeout=900):
    deadline = time.monotonic() + timeout
    lines = []
    while time.monotonic() < deadline:
        lines = logs(ctx, since, until, app=app, contains=contains)
        if predicate(lines):
            break
        time.sleep(30)
    return lines


def app_metrics(ctx, app, since, until):
    """CPU (cores) and working set (MiB) per minute, from Container Apps metrics."""
    out = {}
    for metric in ["UsageNanoCores", "WorkingSetBytes"]:
        view = az_json("monitor", "metrics", "list", "--resource", app_path(ctx, app), "--metric", metric,
                       "--interval", "PT1M", "--aggregation", "Average", "Maximum",
                       "--start-time", since, "--end-time", until, check=False) or {}
        points = []
        for entry in (view.get("value") or [{}])[0].get("timeseries", []):
            points += [p for p in entry.get("data", []) if p.get("average") is not None]
        scale = 1e9 if metric == "UsageNanoCores" else 1024 * 1024
        averages = [p["average"] / scale for p in points]
        maxima = [(p.get("maximum") or p["average"]) / scale for p in points]
        out[metric] = {"minutes": len(points), "mean": round(sum(averages) / len(averages), 3) if averages else None,
                       "max_minute_mean": round(max(averages), 3) if averages else None,
                       "max": round(max(maxima), 3) if maxima else None}
    return {"cpu_cores": out["UsageNanoCores"], "working_set_mib": out["WorkingSetBytes"]}


def vault_metrics(ctx, since, until):
    """Key Vault API results by status code, per minute: throttling shows as 429."""
    view = az_json("monitor", "metrics", "list", "--resource", ctx.state["vault_id"], "--metric", "ServiceApiResult",
                   "--interval", "PT1M", "--aggregation", "Count", "--start-time", since, "--end-time", until,
                   "--filter", "StatusCode eq '*'", check=False) or {}
    totals, peak = {}, {}
    for entry in (view.get("value") or [{}])[0].get("timeseries", []):
        code = ",".join(m["value"] for m in entry.get("metadatavalues", [])) or "all"
        counts = [p.get("count") or 0 for p in entry.get("data", [])]
        totals[code] = int(sum(counts))
        peak[code] = int(max(counts, default=0))
    return {"by_status": totals, "busiest_minute_by_status": peak}


# ---------------------------------------------------------------------------------------------
# Smoke test and the operator's CLI
# ---------------------------------------------------------------------------------------------

def smoke_test(ctx):
    """app-b's first conversion of appb-smoke (waiting for the service's roles), then its delete."""
    started = time.monotonic()
    attempts = []
    while time.monotonic() - started < 1500:
        result = client_once(ctx, "appb-smoke", "app-b")
        attempts.append({k: result.get(k) for k in ["status", "s", "kind", "detail"]})
        if result.get("status") == 200 and result.get("pdf"):
            break
        log(f"smoke: {classify(result)}; waiting")
        time.sleep(30)
    deleted = client_delete(ctx, "appb-smoke", "app-b")
    return {"attempts": attempts, "until_pdf_s": elapsed(started), "delete": deleted}


class Cli:
    """The provisioner on this machine, with the Azure CLI's login, for list, disable, and enable;
    none of them needs the renderers' ports, so the allow-list stays the NAT gateway's alone."""

    def __init__(self, ctx):
        self.ctx = ctx
        env = {k: v for k, v in os.environ.items() if not k.startswith("Provisioner__")}
        env.update({k: v for k, v in provisioner_env(ctx, ctx.state["service_settings"]["disk"]).items()
                    if not k.startswith("Logging__") and "ManagedIdentityClientId" not in k})
        self.env = env

    def __call__(self, *args, check=False):
        count = self.ctx.state.get("cli_count", 0) + 1
        self.ctx.state.save(cli_count=count)
        started = time.monotonic()
        import subprocess
        process = subprocess.Popen(
            ["dotnet", str(BIN / "Atli.Reports.Provisioner/release/atli-reports-provisioner.dll"), *args],
            env=self.env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        lines = [(elapsed(started), redact(line.rstrip("\n"))) for line in process.stdout]
        code = process.wait()
        seconds = elapsed(started)
        path = self.ctx.work / "logs" / f"cli-{count:03d}-{args[0]}.log"
        path.write_text(f"$ provisioner {' '.join(args)}\n" + "".join(f"[{at:7.2f}] {l}\n" for at, l in lines)
                        + f"exit {code} after {seconds} s\n")
        log(f"provisioner {' '.join(args)}: exit {code} in {seconds} s")
        if check and code != 0:
            raise RuntimeError(f"provisioner {' '.join(args)} exited {code}")
        return {"exit": code, "seconds": seconds, "lines": [l for _, l in lines]}


# ---------------------------------------------------------------------------------------------
# Scenario 1: isolation
# ---------------------------------------------------------------------------------------------

def probe_https(connect, sni, host, path="/health/live"):
    """From this machine, like workspace-client.py probe."""
    result = {}
    try:
        answers = sorted({info[4][0] for info in socket.getaddrinfo(sni, 443)})
        result["dns"] = ["private" if a.startswith(("10.", "192.168.", "172.")) else "public" for a in answers]
    except OSError as error:
        result["dns"] = f"error: {type(error).__name__}"
    started = time.monotonic()
    try:
        raw = socket.create_connection((connect, 443), timeout=10)
        sock = ssl.create_default_context().wrap_socket(raw, server_hostname=sni)
        sock.sendall(f"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n".encode())
        data = b""
        while len(data) < 4096:
            chunk = sock.recv(4096)
            if not chunk:
                break
            data += chunk
        sock.close()
        head = data.split(b"\r\n\r\n", 1)
        result["status_line"] = head[0].split(b"\r\n", 1)[0].decode(errors="replace")[:80]
        result["body"] = redact((head[1] if len(head) > 1 else b"")[:160].decode(errors="replace"))
    except (OSError, ssl.SSLError) as error:
        result["error"] = redact(f"{type(error).__name__}: {str(error)[:160]}")
    result["s"] = round(time.monotonic() - started, 3)
    return result


def client_probe(ctx, connect, sni, host, path="/health/live"):
    output = ctx.aca("client").exec(ctx.client, f"cd /root/run && python3 workspace-client.py probe --connect {connect} "
                                                f"--sni {sni} --host {host} --path {path}", check=False).stdout
    line = next((l for l in output.splitlines() if l.startswith("{")), None)
    return json.loads(redact(line)) if line else {"output": redact(output[-300:])}


def isolation(ctx):
    results = {}
    since = utc()
    before = census(ctx)
    checks = [
        ("a_converts_under_b_prefix", lambda: client_once(ctx, "appb-x", "app-a")),
        ("a_deletes_under_b_prefix", lambda: client_delete(ctx, "appb-x", "app-a")),
        ("b_converts_under_a_prefix", lambda: client_once(ctx, "appa-x", "app-b")),
        ("b_deletes_under_a_prefix", lambda: client_delete(ctx, "appa-x", "app-b")),
        ("a_converts_outside_prefixes", lambda: client_once(ctx, "other-1", "app-a")),
        ("a_deletes_outside_prefixes", lambda: client_delete(ctx, "other-1", "app-a")),
        ("a_converts_listed_style_id", lambda: client_once(ctx, "contoso", "app-a")),
        ("a_deletes_listed_style_id", lambda: client_delete(ctx, "contoso", "app-a")),
        ("a_converts_prefix_alone", lambda: client_once(ctx, "appa-", "app-a")),
        ("a_converts_invalid_id", lambda: client_once(ctx, "appa-Bad_Id", "app-a")),
        ("a_deletes_invalid_id", lambda: client_delete(ctx, "appa-Bad_Id", "app-a")),
        ("a_converts_without_header", lambda: client_once(ctx, None, "app-a")),
        ("a_converts_readiness_probe", lambda: client_once(ctx, "readiness-probe", "app-a")),
    ]
    for name, check in checks:
        result = check()
        results[name] = {k: result.get(k) for k in ["status", "kind", "detail", "s"] if result.get(k) is not None}
        log(f"{name}: {results[name].get('status')} {results[name].get('kind')}")
        ctx.record("isolation", results)
    # The provisioning service's internal address.
    service, gateway = ctx.secret["service_fqdn"], ctx.secret["gateway_fqdn"]
    environment_ip = ctx.secret.get("environment_ip")
    laptop = {
        "service_host_direct": probe_https(service, service, service) if _resolves(service) else
        {"dns": "does not resolve"},
        "gateway_name_with_service_host_header": probe_https(gateway, gateway, service),
        "gateway_itself": probe_https(gateway, gateway, gateway),
    }
    client = {
        "service_host_direct": client_probe(ctx, service, service, service),
        "gateway_name_with_service_host_header": client_probe(ctx, gateway, gateway, service),
        "gateway_itself": client_probe(ctx, gateway, gateway, gateway),
    }
    if environment_ip:
        laptop["environment_address_with_service_name"] = probe_https(environment_ip, service, service)
        client["environment_address_with_service_name"] = client_probe(ctx, environment_ip, service, service)
    results["internal_address"] = {"from_laptop": laptop, "from_client": client}
    after = census(ctx)
    results["nothing_created"] = {
        "tenants_before": sorted(before["tenants"]), "tenants_after": sorted(after["tenants"]),
        "service_lines": [], "gateway_events": None,
    }
    ctx.record("isolation", results)
    time.sleep(150)  # ingestion
    until = utc()
    results["nothing_created"]["service_lines"] = [l["line"] for l in logs(ctx, since, until, app=SERVICE)
                                                   if "health" not in l["line"].lower()][:50]
    results["nothing_created"]["gateway_events"] = log_events(ctx, GATEWAY, since, until)
    gateway_lines = logs(ctx, since, until, app=GATEWAY, contains=["[52]", "[69]", "[70]", "[64]"])
    results["gateway_log_excerpt"] = [l["line"] for l in gateway_lines][:30]
    ctx.record("isolation", results)


def _resolves(name):
    try:
        socket.getaddrinfo(name, 443)
        return True
    except OSError:
        return False


# ---------------------------------------------------------------------------------------------
# Scenario 11: a leftover sandbox
# ---------------------------------------------------------------------------------------------

def leftover(ctx):
    results = ctx.results.get("leftover", {})
    rest = ctx.rest("rend")
    if ctx.options.action == "start":
        tenant = LEFTOVER
        body = {
            "sourcesRef": {"diskImage": {"id": ctx.disk("a"), "isPublic": False}},
            "resources": {"cpu": "500m", "memory": "1024Mi"},
            "egressPolicy": {"defaultAction": "Deny"},
            "entrypoint": spike.ENTRYPOINT,
            "environment": prod.renderer_environment("S"),
            "labels": {"app": "atli-reports", "role": "renderer", "tenant": tenant, "size": "S",
                       "launch": "leftover-probe"},
            "lifecycle": {"autoSuspendPolicy": {"enabled": True, "interval": 60, "mode": "Memory"}},
            "customerVnetConnectionName": "renderers",
        }
        created_utc = utc()
        status, view, seconds = rest.timed("PUT", "sandboxes", body)
        if status >= 300:
            raise RuntimeError(f"creating the leftover answered {status}: {redact(json.dumps(view))[:300]}")
        ctx.state.save(leftover_sandbox=view["id"], leftover_created_utc=created_utc)
        results = {"tenant": tenant, "sandbox": view["id"], "created_utc": created_utc,
                   "created_at": view.get("createdAt"), "create_call_s": round(seconds, 2),
                   "record": read_record(ctx, tenant)}
        ctx.record("leftover", results)
        log(f"leftover sandbox {view['id']} created for {tenant}, with no record")
        return
    sandbox = ctx.state["leftover_sandbox"]
    started = time.monotonic()
    while rest.sandbox(sandbox) is not None:
        if time.monotonic() - started > ctx.options.timeout:
            results["still_there_after_s"] = elapsed(started)
            ctx.record("leftover", results)
            return
        time.sleep(15)
    results["gone_observed_utc"] = utc()
    lines = wait_logs(ctx, ctx.state["leftover_created_utc"], None, SERVICE,
                      lambda ls: any("Deleted the leftover" in l["line"] for l in ls), contains=[sandbox, "[20]"])
    results["service_lines"] = [f"{l['time']} {l['line']}" for l in lines if sandbox in l["line"]]
    results["retirement_runs"] = [f"{l['time']} {l['line']}" for l in lines if "[20]" in l["line"]][:15]
    ctx.record("leftover", results)


# ---------------------------------------------------------------------------------------------
# Scenarios 2 and 3: new workspaces at once
# ---------------------------------------------------------------------------------------------

def first_pdf_summary(records):
    """Per tenant: attempts, the answers before the PDF, and seconds from the start to its end."""
    tenants = collections.defaultdict(list)
    for r in records:
        tenants[(r["tenant"], r.get("copy", 0))].append(r)
    per = {}
    for (tenant, copy), rs in sorted(tenants.items()):
        rs.sort(key=lambda r: r["t"])
        pdf = next((r for r in rs if r["status"] == 200 and r.get("pdf")), None)
        per[f"{tenant}#{copy}" if copy else tenant] = {
            "attempts": len(rs), "answers": [classify(r) for r in rs],
            "first_pdf_done_s": round(pdf["t"] + pdf["s"], 2) if pdf else None,
            "pdf_request_s": pdf and pdf["s"],
        }
    done = [v["first_pdf_done_s"] for v in per.values() if v["first_pdf_done_s"] is not None]
    return per, done


def service_created_lines(ctx, since, until, match):
    lines = logs(ctx, since, until, app=SERVICE, contains=["[1]", "[2]", "[5]", "[6]", "after the create call", "Created sandbox"])
    return [l for l in lines if match(l["line"])]


def burst(ctx):
    tenants = [f"appa-b{i:02d}" for i in range(1, 41)]
    results = {"tenants": len(tenants)}
    results["before"] = census_brief(census(ctx))
    data = client_streams(ctx, "burst", [{"name": "burst", "type": "burst", "key_file": "app-a.key",
                                          "tenants": tenants, "duration": 600, "max_wait": 65}], 900)
    records = data["records"]
    per, done = first_pdf_summary(records)
    results.update({
        "since_utc": data["since_utc"], "until_utc": data["until_utc"],
        "requests": len(records), "answers": by_class(records),
        "first_pdf_done_s": stats(done), "all_done_s": max(done) if done else None,
        "tenants_with_pdf": len(done),
        "pdf_request_s": stats([v["pdf_request_s"] for v in per.values() if v["pdf_request_s"]]),
        "attempts_per_tenant": dict(collections.Counter(v["attempts"] for v in per.values())),
        "per_tenant": per,
        "timeline_pdfs_by_10s": dict(collections.Counter(str(int(d // 10) * 10) for d in done)),
    })
    ctx.record("burst", results)
    after = census(ctx, match=lambda t: t.startswith("appa-b"))
    results["after"] = census_brief(after)
    results["after_tenants"] = {t: {"sandboxes": len(v["sandboxes"]), "record_names_one": v["record_sandbox"] in
                                    [s["id"] for s in v["sandboxes"]]} for t, v in after["tenants"].items()}
    results["exactly_40"] = (after["by_prefix"]["appa-"]["records"] == 40 and after["by_prefix"]["appa-"]["sandboxes"] == 40
                             and not after["duplicates"] and not after["leftovers"]
                             and not after["records_without_sandbox"])
    ctx.record("burst", results)
    time.sleep(180)
    until = utc()
    lines = service_created_lines(ctx, data["since_utc"], until, lambda l: "appa-b" in l)
    save_log(ctx, "service-burst", lines)
    results["service_events"] = log_events(ctx, SERVICE, data["since_utc"], until)
    results["gateway_events"] = log_events(ctx, GATEWAY, data["since_utc"], until)
    created = [l for l in lines if "[1]" in l["line"]]
    results["service_created_times"] = [l["time"] for l in created]
    results["service_ready_after_s"] = stats([float(m.group(1)) for l in lines
                                              for m in [re.search(r"Ready ([0-9.]+) s after the create call", l["line"])] if m])
    results["service_excerpt"] = [f"{l['time']} {l['line']}" for l in lines[:12]]
    ctx.record("burst", results)


def same(ctx):
    tenant = "appa-s01"
    results = {"tenant": tenant, "requests_at_once": 10}
    data = client_streams(ctx, "same", [{"name": "same", "type": "burst", "key_file": "app-a.key",
                                         "tenants": [tenant], "copies": 10, "duration": 300, "max_wait": 65}], 600)
    records = data["records"]
    per, done = first_pdf_summary(records)
    results.update({
        "since_utc": data["since_utc"], "until_utc": data["until_utc"],
        "requests": len(records), "answers": by_class(records),
        "first_attempt_answers": by_class([r for r in records if r.get("attempt") == 1]),
        "first_attempt_s": stats([r["s"] for r in records if r.get("attempt") == 1 and r["status"] == 200]),
        "pdfs": len(done), "first_pdf_done_s": stats(done), "per_request": per,
    })
    after = census(ctx, match=lambda t: t == tenant)
    results["after"] = after["tenants"].get(tenant)
    results["one_sandbox"] = len((after["tenants"].get(tenant) or {}).get("sandboxes", [])) == 1
    ctx.record("same", results)
    time.sleep(180)
    until = utc()
    service = logs(ctx, data["since_utc"], until, app=SERVICE, contains=[tenant])
    gateway = logs(ctx, data["since_utc"], until, app=GATEWAY, contains=[tenant])
    results["service_lines"] = [f"{l['time']} {l['line']}" for l in service]
    results["gateway_ensure_calls"] = sum("[64]" in l["line"] for l in gateway)
    results["gateway_events"] = dict(collections.Counter(m.group(0) for l in gateway
                                                         for m in [re.search(r"\w+\[\d+\]", l["line"])] if m))
    ctx.record("same", results)


# ---------------------------------------------------------------------------------------------
# Scenario 4: the quota
# ---------------------------------------------------------------------------------------------

def quota(ctx):
    results = {"since_utc": utc(), "creates": {}}
    for index in range(1, 7):
        tenant = f"appb-q{index}"
        result = client_once(ctx, tenant, "app-b")
        results["creates"][tenant] = {k: result.get(k) for k in ["status", "s", "kind", "detail", "retry_after"]
                                      if result.get(k) is not None}
        log(f"{tenant}: {classify(result)} in {result.get('s')} s")
        ctx.record("quota", results)
    # The sixth again, a minute later: still the quota, not the rate limit.
    time.sleep(60)
    again = client_once(ctx, "appb-q6", "app-b")
    results["sixth_again_after_60s"] = {k: again.get(k) for k in ["status", "s", "kind", "detail", "retry_after"]
                                        if again.get(k) is not None}
    after = census(ctx, match=lambda t: t.startswith("appb-"))
    results["after"] = census_brief(after)
    results["exactly_5"] = (after["by_prefix"]["appb-"]["records"] == 5 and after["by_prefix"]["appb-"]["sandboxes"] == 5)
    ctx.record("quota", results)
    # Room for scenario 5: app-b deletes four of them through the gateway.
    deletes = {}
    for index in range(2, 6):
        deletes[f"appb-q{index}"] = client_delete(ctx, f"appb-q{index}", "app-b")
    results["deleted_for_fairness"] = {t: {k: v.get(k) for k in ["status", "s", "kind"] if v.get(k) is not None}
                                       for t, v in deletes.items()}
    results["after_deletes"] = census_brief(census(ctx, match=lambda t: t.startswith("appb-")))
    results["until_utc"] = utc()
    ctx.record("quota", results)
    time.sleep(150)
    lines = logs(ctx, results["since_utc"], utc(), app=SERVICE, contains=["appb-q6"])
    results["service_lines_q6"] = [f"{l['time']} {l['line']}" for l in lines][:10]
    ctx.record("quota", results)


# ---------------------------------------------------------------------------------------------
# Scenario 5: fairness
# ---------------------------------------------------------------------------------------------

def fairness(ctx):
    results = {}
    warm, cold = ["appa-b01", "appa-b02", "appa-b03"], ["appa-b04", "appa-b05", "appa-b06"]
    # app-a's warm workspaces were converted moments ago; its cold ones not since the burst, so the
    # gateway has forgotten them (it keeps a tenant that had a renderer ten minutes or so).
    for tenant in warm:
        client_once(ctx, tenant, "app-a")
    results["before"] = census_brief(census(ctx))
    results["burst_ended_utc"] = ctx.results.get("burst", {}).get("until_utc")
    duration = ctx.options.duration
    streams = [
        {"name": "a-flood", "type": "flood", "key_file": "app-a.key", "prefix": "appa-f", "workers": 8,
         "processes": 4, "duration": duration},
        {"name": "a-warm", "type": "periodic", "key_file": "app-a.key", "tenants": warm, "interval": 5,
         "duration": duration + 10},
        {"name": "a-cold", "type": "periodic", "key_file": "app-a.key", "tenants": cold, "interval": 5,
         "duration": duration + 10, "retry": True, "max_wait": 5},
        {"name": "b-existing", "type": "periodic", "key_file": "app-b.key", "tenants": ["appb-q1"], "interval": 2,
         "duration": duration + 10},
        {"name": "b-new-1", "type": "periodic", "key_file": "app-b.key", "tenants": ["appb-f1"], "interval": 5,
         "start": 10, "duration": duration, "retry": True},
        {"name": "b-new-2", "type": "periodic", "key_file": "app-b.key", "tenants": ["appb-f2"], "interval": 5,
         "start": 30, "duration": duration - 20, "retry": True},
    ]
    data = client_streams(ctx, "fairness", streams, duration + 900)
    records = data["records"]
    since, until = data["since_utc"], data["until_utc"]
    per_stream = {}
    for name in [s["name"] for s in streams]:
        rs = [r for r in records if r["stream"] == name]
        entry = {"requests": len(rs), "answers": by_class(rs),
                 "pdf_s": stats([r["s"] for r in rs if r["status"] == 200 and r.get("pdf")])}
        if name == "a-flood":
            entry["requests_per_s"] = round(len(rs) / duration, 1)
            entry["tenants_named"] = len({r["tenant"] for r in rs})
            entry["refusal_s"] = {c: stats([r["s"] for r in rs if classify(r) == c])
                                  for c in entry["answers"] if c != "pdf"}
            entry["pdfs_by_10s"] = dict(collections.Counter(str(int(r["t"] // 10) * 10) for r in rs
                                                            if r["status"] == 200 and r.get("pdf")))
            entry["first_quota_t"] = min((r["t"] for r in rs if "quota" in classify(r)), default=None)
        if name.startswith("b-new"):
            pdfs = [r for r in rs if r["status"] == 200 and r.get("pdf")]
            entry["first_request_t"] = rs[0]["t"] if rs else None
            entry["first_pdf_done_t"] = round(pdfs[0]["t"] + pdfs[0]["s"], 2) if pdfs else None
            entry["first_answers"] = [classify(r) for r in rs[:4]]
        if name in ("a-cold",):
            entry["first_pdf_done_t"] = {t: next((round(r["t"] + r["s"], 2) for r in rs if r["tenant"] == t
                                                  and r["status"] == 200 and r.get("pdf")), None) for t in cold}
        per_stream[name] = entry
    results.update({"since_utc": since, "until_utc": until, "duration_s": duration, "streams": per_stream})
    ctx.record("fairness", results)
    after = census(ctx)
    results["after"] = census_brief(after)
    results["flood_tenants_with_renderer"] = sorted(t for t in after["tenants"] if t.startswith("appa-f"))
    ctx.record("fairness", results)
    time.sleep(240)
    end = utc()
    results["gateway_events"] = log_events(ctx, GATEWAY, since, end)
    results["service_events"] = log_events(ctx, SERVICE, since, end)
    results["vault_api_results"] = vault_metrics(ctx, since, end)
    results["gateway_metrics"] = app_metrics(ctx, GATEWAY, since, end)
    results["service_metrics"] = app_metrics(ctx, SERVICE, since, end)
    directory_failures = logs(ctx, since, end, app=GATEWAY, contains=["directory", "Directory", "[41]", "[42]"])
    results["gateway_directory_failures"] = [l["line"] for l in directory_failures][:20]
    ctx.record("fairness", results)


def trim(ctx):
    """app-a deletes the workspaces the flood made, two at a time (its limit), through the gateway."""
    found = census(ctx, match=lambda t: t.startswith("appa-f"))
    tenants = sorted(found["tenants"])
    results = {"tenants": len(tenants)}
    if tenants:
        data = client_streams(ctx, "trim", [{"name": "trim", "type": "delete", "key_file": "app-a.key",
                                             "tenants": tenants, "workers": 2}], 1200)
        results["answers"] = by_class(data["records"])
        results["delete_s"] = stats([r["s"] for r in data["records"] if r["status"] == 204])
    after = census(ctx, match=lambda t: t.startswith("appa-f"))
    results["after"] = census_brief(after)
    results["census"] = census_brief(census(ctx))
    ctx.record("trim", results)


# ---------------------------------------------------------------------------------------------
# Scenario 6: steady use
# ---------------------------------------------------------------------------------------------

def steady(ctx):
    gone = ctx.state.get("repro_deleted", [])
    existing = [f"appa-b{i:02d}" for i in range(7, 27) if f"appa-b{i:02d}" not in gone]
    new = [f"appa-n{i:02d}" for i in range(1, 31 - len(existing))]
    # Less the tenant the listing bug's reproduction deleted, which would be created, not woken.
    sparse = [f"appa-b{i:02d}" for i in range(27, 32) if f"appa-b{i:02d}" not in ctx.state.get("repro_deleted", [])]
    duration = ctx.options.duration
    results = {"existing": existing, "new": new, "sparse": sparse, "duration_s": duration}
    results["before"] = census_brief(census(ctx))
    states = census(ctx, match=lambda t: t in existing or t in sparse)
    results["states_before"] = dict(collections.Counter(s["state"] for v in states["tenants"].values()
                                                        for s in v["sandboxes"]))
    streams = [
        {"name": "steady", "type": "periodic", "key_file": "app-a.key", "tenants": existing + new, "interval": 5,
         "report_every": 20, "duration": duration},
        {"name": "sparse", "type": "periodic", "key_file": "app-a.key", "tenants": sparse, "interval": 180,
         "duration": duration},
    ]
    data = client_streams(ctx, "steady", streams, duration + 900)
    records = data["records"]
    since, until = data["since_utc"], data["until_utc"]
    ok = lambda r: r["status"] == 200 and r.get("pdf")  # noqa: E731
    steady_records = [r for r in records if r["stream"] == "steady"]
    groups = {
        "first_created": [r for r in steady_records if r.get("seq") == 1 and r["tenant"] in new],
        "first_woken": [r for r in steady_records if r.get("seq") == 1 and r["tenant"] in existing],
        "warm_invoice": [r for r in steady_records if r.get("seq", 0) > 1 and r["fixture"] == "invoice"],
        "warm_report": [r for r in steady_records if r.get("seq", 0) > 1 and r["fixture"] == "long-table"],
        "sparse_first": [r for r in records if r["stream"] == "sparse" and r.get("seq") == 1],
        "sparse_after_180s_idle": [r for r in records if r["stream"] == "sparse" and r.get("seq", 0) > 1],
    }
    results.update({
        "since_utc": since, "until_utc": until,
        "requests": len(records), "pdfs": sum(1 for r in records if ok(r)),
        "success_rate": round(sum(1 for r in records if ok(r)) / max(1, len(records)), 5),
        "answers": by_class(records),
        "latency_s": {name: dict(stats([r["s"] for r in rs if ok(r)]), failed=sum(1 for r in rs if not ok(r)))
                      for name, rs in groups.items()},
        "non_pdf": [{k: r.get(k) for k in ["t", "stream", "tenant", "status", "kind", "detail", "s", "fixture"]}
                    for r in records if not ok(r)][:50],
    })
    ctx.record("steady", results)
    results["after"] = census_brief(census(ctx))
    ctx.record("steady", results)
    time.sleep(240)
    end = utc()
    results["gateway_metrics"] = app_metrics(ctx, GATEWAY, since, until)
    results["service_metrics"] = app_metrics(ctx, SERVICE, since, until)
    results["gateway_events"] = log_events(ctx, GATEWAY, since, end)
    results["service_events"] = log_events(ctx, SERVICE, since, end)
    results["vault_api_results"] = vault_metrics(ctx, since, end)
    ctx.record("steady", results)


# ---------------------------------------------------------------------------------------------
# Scenario 7: delete
# ---------------------------------------------------------------------------------------------

def delete(ctx):
    tenants = [f"appa-b{i:02d}" for i in range(32, 37)]
    results = {"since_utc": utc()}
    before = census(ctx, match=lambda t: t in tenants)
    results["before"] = {t: {"record": bool(v["record_sandbox"]), "sandboxes": len(v["sandboxes"]),
                             "states": [s["state"] for s in v["sandboxes"]]} for t, v in before["tenants"].items()}
    results["deletes"] = {}
    for tenant in tenants:
        result = client_delete(ctx, tenant, "app-a")
        results["deletes"][tenant] = {k: result.get(k) for k in ["status", "s", "kind", "detail"] if result.get(k) is not None}
        log(f"DELETE {tenant}: {result.get('status')} in {result.get('s')} s")
    after = census(ctx, match=lambda t: t in tenants)
    sandboxes = list_sandboxes(ctx)
    results["after"] = {
        "records": {t: read_record(ctx, t) is not None for t in tenants},
        "secrets_enabled": {t: any(n == f"renderer-{t}" and e for n, e in vault_secret_names(ctx)) for t in tenants},
        "sandboxes_labeled": {t: sum(1 for s in sandboxes if renderer_tenant(s) == t) for t in tenants},
        "census": census_brief(after),
    }
    ctx.record("delete", results)
    again = client_once(ctx, tenants[0], "app-a")
    results["recreate"] = {"tenant": tenants[0], **{k: again.get(k) for k in ["status", "s", "kind", "detail"]
                                                    if again.get(k) is not None}}
    results["recreate"]["after"] = census(ctx, match=lambda t: t == tenants[0])["tenants"].get(tenants[0])
    results["until_utc"] = utc()
    ctx.record("delete", results)
    time.sleep(150)
    lines = logs(ctx, results["since_utc"], utc(), app=SERVICE, contains=tenants)
    results["service_lines"] = [f"{l['time']} {l['line']}" for l in lines][:40]
    gateway = logs(ctx, results["since_utc"], utc(), app=GATEWAY, contains=tenants)
    results["gateway_lines"] = [f"{l['time']} {l['line']}" for l in gateway][:20]
    ctx.record("delete", results)


# ---------------------------------------------------------------------------------------------
# Scenario 8: the kill switch
# ---------------------------------------------------------------------------------------------

def killswitch(ctx):
    tenant = "appa-b37"
    cli = Cli(ctx)
    results = {"tenant": tenant, "since_utc": utc()}

    def snapshot():
        c = census(ctx, match=lambda t: t == tenant)["tenants"].get(tenant) or {}
        return {"record_sandbox": c.get("record_sandbox"), "sandboxes": c.get("sandboxes")}

    results["before"] = snapshot()
    results["warm_up"] = client_once(ctx, tenant, "app-a")
    disabled = cli("disable", "--tenant", tenant)
    results["disable"] = {"exit": disabled["exit"], "seconds": disabled["seconds"], "lines": disabled["lines"]}
    results["after_disable"] = snapshot()
    results["delete_while_disabled"] = client_delete(ctx, tenant, "app-a")
    results["after_refused_delete"] = snapshot()
    results["conversions_while_disabled"] = [client_once(ctx, tenant, "app-a") for _ in range(2)]
    time.sleep(35)  # past the gateway's record cache
    results["conversion_while_disabled_after_cache"] = client_once(ctx, tenant, "app-a")
    results["after_conversions"] = snapshot()
    enabled = cli("enable", "--tenant", tenant)
    results["enable"] = {"exit": enabled["exit"], "seconds": enabled["seconds"], "lines": enabled["lines"]}
    results["conversions_after_enable"] = [client_once(ctx, tenant, "app-a") for _ in range(2)]
    results["after_enable"] = snapshot()
    listed = cli("list")
    results["list_lines"] = [l for l in listed["lines"] if tenant in l or l.startswith("TENANT")]
    results["until_utc"] = utc()
    ctx.record("killswitch", results)
    time.sleep(150)
    results["gateway_lines"] = [f"{l['time']} {l['line']}" for l in
                                logs(ctx, results["since_utc"], utc(), app=GATEWAY, contains=[tenant])][:30]
    results["service_lines"] = [f"{l['time']} {l['line']}" for l in
                                logs(ctx, results["since_utc"], utc(), app=SERVICE, contains=[tenant])][:30]
    ctx.record("killswitch", results)


# ---------------------------------------------------------------------------------------------
# Scenario 9: retirement
# ---------------------------------------------------------------------------------------------

def retire(ctx):
    results = ctx.results.get("retire", {})
    busy, watched = "appa-b38", ["appa-b39", "appa-b40", "appa-s01"]
    # The busy one converts every 20 s throughout, from before the service retires anything.
    client_start(ctx, "retire-busy", [{"name": "busy", "type": "periodic", "key_file": "app-a.key",
                                       "tenants": [busy], "interval": 20, "duration": ctx.options.duration}])
    time.sleep(10)
    busy_before = census(ctx, match=lambda t: t == busy)["tenants"].get(busy, {}).get("record_sandbox")
    if "revision" not in results:
        results["before"] = census_brief(census(ctx))
        deploy_service(ctx, retire_after_idle="00:05:00", retire_check="00:01:00")
        results["revision"] = ctx.state["deploys"][-1]
        ctx.record("retire", results)
    since = results["revision"]["since_utc"]
    # The watched ones convert once now, then sit idle.
    results["watched_used"] = {t: client_once(ctx, t, "app-a") for t in watched}
    results["watched_used_utc"] = utc()
    # Poll the group until the watched tenants are retired.
    timeline = []
    started = time.monotonic()
    retired_at = {}
    stopped_at = {}
    while time.monotonic() - started < ctx.options.duration - 60 and len(retired_at) < len(watched):
        c = census(ctx)
        entry = {"at_utc": c["at_utc"], "by_prefix": c["by_prefix"]}
        for t in watched:
            v = c["tenants"].get(t)
            if t not in retired_at and (v is None or not v["record_sandbox"]):
                retired_at[t] = c["at_utc"]
            elif v and v["sandboxes"] and v["sandboxes"][0].get("stopped_at"):
                stopped_at[t] = v["sandboxes"][0]["stopped_at"]
        timeline.append(entry)
        results.update({"timeline": timeline[-40:], "retired_observed_utc": retired_at, "stopped_at": stopped_at})
        ctx.record("retire", results)
        time.sleep(30)
    # Their next conversion creates a new renderer.
    results["recreated"] = {t: client_once(ctx, t, "app-a") for t in retired_at}
    busy_data = client_collect(ctx, "retire-busy", ctx.options.duration + 300)
    busy_records = busy_data["records"]
    busy_after = census(ctx, match=lambda t: t == busy)["tenants"].get(busy, {}).get("record_sandbox")
    results["busy"] = {"tenant": busy, "requests": len(busy_records), "answers": by_class(busy_records),
                       "same_sandbox_throughout": busy_before == busy_after and busy_before is not None}
    results["after"] = census_brief(census(ctx))
    results["until_utc"] = utc()
    ctx.record("retire", results)
    time.sleep(180)
    lines = logs(ctx, since, utc(), app=SERVICE, contains=["[20]", "[21]", "[22]", "Retiring", "Not retired",
                                                           "Retired", "leftover"])
    save_log(ctx, "service-retire", lines)
    results["retirement_runs"] = [f"{l['time']} {l['line']}" for l in lines if "[20]" in l["line"] or "[22]" in l["line"]]
    results["watched_lines"] = [f"{l['time']} {l['line']}" for l in lines if any(t in l["line"] for t in watched)]
    results["busy_lines"] = [f"{l['time']} {l['line']}" for l in lines if busy in l["line"]]
    results["retiring_lines"] = sum("Retiring" in l["line"] for l in lines)
    ctx.record("retire", results)


# ---------------------------------------------------------------------------------------------
# Scenario 10: a rollout that retires stopped renderers
# ---------------------------------------------------------------------------------------------

def run_job(ctx, args):
    """Runs the provisioner once as a Container Apps job in the environment, with the service's
    identity and settings: its requests leave through the NAT gateway, which the ports admit."""
    env = provisioner_env(ctx, ctx.state["service_settings"]["disk"])
    identity = ctx.state["identity_service_id"]
    body = {
        "location": ctx.region, "tags": {"purpose": PURPOSE},
        "identity": {"type": "UserAssigned", "userAssignedIdentities": {identity: {}}},
        "properties": {
            "environmentId": ctx.state["environment_id"], "workloadProfileName": "Consumption",
            "configuration": {
                "triggerType": "Manual", "replicaTimeout": 1800, "replicaRetryLimit": 0,
                "manualTriggerConfig": {"parallelism": 1, "replicaCompletionCount": 1},
                "registries": [{"server": ctx.secret["registry_server"], "identity": identity}],
            },
            "template": {"containers": [{
                "name": "provisioner-job", "image": image(ctx, "provisioner"),
                "command": ["dotnet", "atli-reports-provisioner.dll"], "args": args,
                "resources": {"cpu": 0.5, "memory": "1Gi"},
                "env": [{"name": n, "value": v} for n, v in env.items()],
            }]},
        },
    }
    prod.arm("PUT", job_path(ctx), body)
    started = time.monotonic()
    while (prod.arm("GET", job_path(ctx))["properties"].get("provisioningState")) != "Succeeded":
        if time.monotonic() - started > 600:
            raise RuntimeError("The job was not created")
        time.sleep(5)
    since = utc()
    execution = prod.arm("POST", f"{job_path(ctx)}/start", {})
    name = execution.get("name") or execution.get("id", "").rsplit("/", 1)[-1]
    return name, since


def job_status(ctx, name):
    view = prod.arm("GET", f"{job_path(ctx)}/executions/{name}", check=False) or {}
    properties = view.get("properties", {})
    return properties.get("status"), properties.get("startTime"), properties.get("endTime")


def rollout(ctx):
    results = ctx.results.get("rollout", {})
    running, stopped = [f"appa-l{i:02d}" for i in range(1, 5)], [f"appa-r{i:02d}" for i in range(1, 5)]
    if "prepared" not in results:
        # Renderers on disk a: four to keep running under light load, four to stop.
        deploy_service(ctx, retire_after_idle="00:00:00", disk="a")
        results["prepared"] = {t: client_once(ctx, t, "app-a") for t in running + stopped}
        results["prepared_utc"] = utc()
        ctx.record("rollout", results)
    # Light load on the running ones from now until after the rollout and its drain.
    load_start = client_start(ctx, "rollout-load", [{"name": "light", "type": "periodic", "key_file": "app-a.key",
                                                     "tenants": running, "interval": 2,
                                                     "duration": ctx.options.duration}])
    if "service_on_b" not in results:
        # The service first, so retired tenants come back on disk b.
        deploy_service(ctx, retire_after_idle="00:00:00", disk="b")
        results["service_on_b"] = ctx.state["deploys"][-1]
        ctx.record("rollout", results)
    # Wait until the four to stop have auto-suspended.
    started = time.monotonic()
    while time.monotonic() - started < 600:
        c = census(ctx, match=lambda t: t in stopped)
        states = [s["state"] for v in c["tenants"].values() for s in v["sandboxes"]]
        if states and all(s == "Stopped" for s in states):
            break
        time.sleep(15)
    before = census(ctx)
    results["before"] = census_brief(before)
    results["before_tenants"] = {t: {"disk": v["record_disk"], "states": [s["state"] for s in v["sandboxes"]]}
                                 for t, v in before["tenants"].items()}
    name, since = run_job(ctx, ["rollout", "--disk-image", ctx.disk("b"), "--stopped", "retire",
                                "--max-parallel", "4"])
    results["job"] = {"execution": name, "started_utc": since, "load_started_epoch": load_start}
    started = time.monotonic()
    status = None
    while time.monotonic() - started < 1800:
        status, start_time, end_time = job_status(ctx, name)
        if status in ("Succeeded", "Failed", "Stopped", "Degraded"):
            break
        time.sleep(10)
    results["job"].update({"status": status, "start_time": start_time, "end_time": end_time,
                           "observed_s": elapsed(started)})
    ctx.record("rollout", results)
    rollout_finish(ctx)


def rollout_finish(ctx):
    """After the rollout job: the light load's answers during it, the group, `list`, a retired
    workspace's next conversion, and the job's log. `rollout finish` runs it alone."""
    results = ctx.results["rollout"]
    running, stopped = [f"appa-l{i:02d}" for i in range(1, 5)], [f"appa-r{i:02d}" for i in range(1, 5)]
    since, start_time, end_time = (results["job"][k] for k in ["started_utc", "start_time", "end_time"])
    local = ctx.work / "client" / "records-rollout-load.json"
    load = json.loads(local.read_text()) if local.exists() else client_collect(ctx, "rollout-load", 1500)
    records = load["records"]
    epoch = load["start_at_epoch"]

    def when(text):
        import datetime
        return datetime.datetime.fromisoformat(text.replace("Z", "+00:00")).timestamp() if text else None

    job_from, job_to = when(start_time), when(end_time)
    during = [r for r in records if job_from and job_to and job_from <= epoch + r["t"] <= job_to]
    results["load"] = {"requests": len(records), "answers": by_class(records),
                       "during_job": {"requests": len(during), "answers": by_class(during)},
                       "pdf_s": stats([r["s"] for r in records if r["status"] == 200 and r.get("pdf")]),
                       "non_pdf": [r for r in records if not (r["status"] == 200 and r.get("pdf"))][:20],
                       "job_window_s": [round(job_from - epoch, 1), round(job_to - epoch, 1)] if job_from and job_to
                       else None}
    after = census(ctx)
    results["after"] = census_brief(after)
    results["after_tenants"] = {t: {"disk": v["record_disk"], "states": [s["state"] for s in v["sandboxes"]]}
                                for t, v in after["tenants"].items() if t in running + stopped
                                or t in results["before_tenants"]}
    listed = Cli(ctx)("list")
    results["list"] = listed["lines"][:80]
    # A retired one comes back, from the service's disk image.
    back = client_once(ctx, stopped[0], "app-a")
    record = read_record(ctx, stopped[0])
    results["retired_comes_back"] = {"tenant": stopped[0], **{k: back.get(k) for k in ["status", "s", "kind", "detail"]
                                                               if back.get(k) is not None},
                                     "disk": record and record.get("disk")}
    ctx.record("rollout", results)
    time.sleep(240)
    lines = logs(ctx, since, utc(), container="provisioner-job")
    save_log(ctx, "job-rollout", lines)
    results["job_lines"] = [f"{l['time']} {l['line']}" for l in lines][:120]
    ctx.record("rollout", results)


# ---------------------------------------------------------------------------------------------
# verify and cleanup
# ---------------------------------------------------------------------------------------------

def verify(ctx, extra_paths=()):
    values = dict(prod.SENSITIVE)
    for key, value in ctx.secret.items():
        if isinstance(value, str) and len(value) >= 6:
            values.setdefault(f"<{key}>", value)
        elif isinstance(value, list):
            for index, item in enumerate(value):
                values.setdefault(f"<{key} {index}>", str(item))
    files = (sorted((ctx.work / "logs").glob("*")) + sorted((ctx.work / "client").glob("records-*.json"))
             + ([ctx.output] if ctx.output else []) + [Path(p) for p in extra_paths])
    found = {}
    for path in files:
        text = path.read_text(errors="replace") if path.exists() else ""
        for name, value in values.items():
            if value and value in text:
                found.setdefault(str(path.name), []).append(name)
        for pattern, name in [(r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.", "token"),
                              (r"reports-[0-9a-f]{12}\.[0-9a-f]{64}", "api key"),
                              (r"[A-Za-z0-9.-]+\.adcproxy\.io", "port host"),
                              (r"azurecontainerapps\.io", "container apps host")]:
            if re.search(pattern, text):
                found.setdefault(str(path.name), []).append(name)
        for match in _IPV4.finditer(text):
            if not _private(match):
                found.setdefault(str(path.name), []).append(f"public ipv4 at {match.start()}")
                break
    result = {"files": len(files), "values_checked": len(values), "found": found or "none"}
    log(f"verify: {result['files']} files, {result['values_checked']} values, found {result['found']}")
    return result


def guard(name):
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
    if not rg.startswith(PREFIX):
        raise RuntimeError(f"Refusing to clean up {rg}")
    exists = az("group", "exists", "-n", rg).stdout.strip() == "true"
    if exists and not guard(rg):
        raise RuntimeError(f"{rg} lacks the purpose tag; not deleting it.")
    if exists:
        # Container Apps first (the job, the gateway, the service), then the environment.
        for path in [job_path(ctx), app_path(ctx, GATEWAY), app_path(ctx, SERVICE)]:
            prod.arm("DELETE", path, check=False)
        # Role assignments at scopes inside the group (the identities', the operator's, and those
        # the aca CLI made for the sandbox groups' creator), then the custom role definition.
        assignments = az_json("role", "assignment", "list", "--all", "--query",
                              f"[?starts_with(scope, '{ctx.rg_id()}')].id", check=False) or []
        for assignment in assignments:
            az("role", "assignment", "delete", "--ids", assignment, check=False)
        results["role_assignments_deleted"] = len(assignments)
        if ctx.state.get("role_definition_name"):
            az("role", "definition", "delete", "--name", ctx.state["role_definition_name"], "--scope", ctx.rg_id(),
               check=False)
        if ctx.state.get("workspace_id"):
            az("monitor", "log-analytics", "workspace", "delete", "-g", rg, "-n", ctx.state["workspace"],
               "--force", "true", "--yes", check=False)
        if ctx.state.get("environment_id"):
            prod.arm("DELETE", ctx.state["environment_id"], check=False)
        for key in ["client", "rend"]:
            group = ctx.state.get(f"group_{key}")
            if not group or ctx.state.get(f"propagation_{key}") is None:
                continue
            aca = ctx.aca(key)
            if key == "rend" and ctx.state.get("network_connection"):
                aca("sandboxgroup", "network", "delete", "--group", group, "--name", "renderers", "--yes",
                    check=False, timeout=3600)
            started = time.monotonic()
            deleted = aca("sandboxgroup", "delete", "--name", group, "--yes", check=False, timeout=3600)
            results[f"sandbox_group_{key}_delete"] = {"exit": deleted.returncode, "s": elapsed(started)}
            log(f"Sandbox group {group} deleted: exit {deleted.returncode} in {elapsed(started)} s")
        if guard(rg):
            log(f"Deleting {rg}")
            az("group", "delete", "-n", rg, "--yes", "--no-wait", check=False)
    started = time.monotonic()
    for name in [rg, ctx.state.get("infra_rg")]:
        while name and az("group", "exists", "-n", name, check=False).stdout.strip() != "false":
            if time.monotonic() - started > 5400:
                raise RuntimeError(f"{name} still exists")
            time.sleep(20)
    results["resource_groups_gone_after_s"] = elapsed(started)
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
        "role_assignments_in_scope": len(az_json("role", "assignment", "list", "--all", "--query",
                                                 f"[?starts_with(scope, '{ctx.rg_id()}')].id", check=False) or []),
        "role_definition_left": bool(ctx.state.get("role_definition_name") and prod.arm(
            "GET", f"/subscriptions/{ctx.subscription}/providers/Microsoft.Authorization/roleDefinitions/"
                   f"{ctx.state['role_definition_name']}", api="2022-04-01", check=False)),
        "role_definitions_named": len(az_json("role", "definition", "list", "--custom-role-only", "true", "--query",
                                              f"[?roleName=='{ctx.state.get('role_name')}'].name", check=False) or []),
        "assignments_for_identities": sum(len(az_json("role", "assignment", "list", "--all", "--assignee",
                                                      ctx.secret.get(f"identity_{k}_principal_id", "none"),
                                                      "--query", "[].id", check=False) or [])
                                          for k in ["gateway", "service"]),
        "identities_exist": [k for k in ["gateway", "service"] if az(
            "identity", "show", "--ids", ctx.state.get(f"identity_{k}_id", "/none"), "-o", "none",
            check=False).returncode == 0],
        "deleted_workspaces": len(az_json("monitor", "log-analytics", "workspace", "list-deleted-workspaces",
                                          "--query", f"[?name=='{ctx.state.get('workspace')}'].name",
                                          check=False) or []),
    }
    results["checks"] = checks
    results["finished_utc"] = utc()
    ctx.record("cleanup", results)
    log(f"cleanup checks: {checks}")


def rebuild(ctx):
    """A new build round of the server image and disks a and b: deletes the round's disks first.
    Run `setup` again afterwards; it redeploys what uses them."""
    commit = ctx.state["commit"]
    aca = ctx.aca("rend")
    round_ = ctx.state.get("build_round", 1)
    deleted = {}
    for suffix in ["a", "b"]:
        disk = ctx.secret.get(f"disk_{suffix}")
        if disk:
            result = aca("sandboxgroup", "disk", "delete", "--id", disk, check=False, timeout=1800)
            deleted[suffix] = result.returncode
            ctx.secret.save(**{f"disk_{suffix}_round{round_}": disk})
            ctx.secret.pop(f"disk_{suffix}")
    for key in ["image_server", "disk_a_name", "disk_b_name", "disk_a_build_s", "disk_b_build_s", "gateway_deployed"]:
        ctx.state.pop(key, None)
    ctx.secret.save()
    ctx.state.save(build_round=round_ + 1)
    log(f"Build round {round_ + 1}: deleted the disks of round {round_} ({deleted}); building again")
    done = concurrent.futures.Future()
    done.set_result(None)
    with concurrent.futures.ThreadPoolExecutor(2) as pool:
        images = pool.submit(setup_registry, ctx, commit)
        disks = pool.submit(setup_sandbox_groups, ctx, done, commit)
        images.result()
        disks.result()
    rounds = ctx.results.get("build_rounds", [])
    rounds.append({"round": round_ + 1, "deleted_disks_exit": deleted, "at_utc": utc(),
                   "acr_server_build_s": ctx.state.get("acr_server_build_s"),
                   "disk_build_s": {k: ctx.state.get(f"disk_{k}_build_s") for k in ["a", "b"]}})
    ctx.record("build_rounds", rounds)
    ctx.note("Build round 1 extracted the server's build context under umask 077, so appsettings.json "
             "reached the image as 0600 root and the server (UID 1654) exited with code 134 at startup. Round 2 "
             "gave the context a default checkout's modes (normalize_modes) and rebuilt the server image and "
             "disks a and b from the same commit; the provisioner image, whose /app is generated in the "
             "build, was unaffected and kept.")


def listing(ctx):
    """The raw GET sandboxes answer: its shape, headers, and count; and each record's sandbox read
    one by one (GET sandboxes/{id})."""
    rest = ctx.rest("rend")
    url = f"{rest.base}/sandboxes?api-version={prod.API_VERSION}"
    request = urllib.request.Request(url, headers={"Authorization": f"Bearer {prod.data_plane_token()}"})
    with urllib.request.urlopen(request, timeout=120) as response:
        headers = {k: v for k, v in response.headers.items() if k.lower() not in ("set-cookie",)}
        body = json.loads(response.read())
    shape = type(body).__name__
    items = body if isinstance(body, list) else body.get("value", [])
    print(redact(json.dumps({"shape": shape, "keys": list(body)[:10] if isinstance(body, dict) else None,
                             "count": len(items), "headers": headers}, indent=1)))
    records = read_records(ctx)
    listed = {s["id"] for s in items}
    missing = {t: r["sandboxId"] for t, r in records.items() if r.get("sandboxId") not in listed}
    found = {t: (rest.sandbox(i) or {}).get("state", "absent") for t, i in missing.items()}
    print(json.dumps({"records": len(records), "listed": len(listed), "records_not_listed": len(missing),
                      "their_state_by_get": collections.Counter(found.values())}, indent=1))
    for parameter in ["$top=100", "top=100", "maxResults=100", "pageSize=100", "$skip=25", "skip=25"]:
        probe = urllib.request.Request(f"{url}&{parameter}",
                                       headers={"Authorization": f"Bearer {prod.data_plane_token()}"})
        try:
            with urllib.request.urlopen(probe, timeout=120) as response:
                view = json.loads(response.read())
                status = response.status
        except urllib.error.HTTPError as error:
            status, view = error.code, None
        count = len(view) if isinstance(view, list) else len(view.get("value", [])) if isinstance(view, dict) else None
        ids = {s["id"] for s in view} if isinstance(view, list) else set()
        print(parameter, status, count, "new ids:", len(ids - listed))


def provisioner_image(ctx):
    """Builds the provisioner image from HEAD (a fix since the run's commit) and rolls the service
    onto it; the CLI on this machine is rebuilt from the same tree."""
    head = run(["git", "-C", str(REPO), "rev-parse", "--short", "HEAD"]).stdout.strip()
    previous = ctx.state.get("image_provisioner")
    ctx.state.pop("image_provisioner", None)
    ctx.state.save(provisioner_commit=head)
    commit = ctx.state["commit"]
    ctx.state.save(commit=head)
    try:
        setup_registry(ctx, head)
    finally:
        ctx.state.save(commit=commit)
    images = ctx.results.get("provisioner_images", [])
    images.append({"commit": head, "replaces": previous, "acr_build_s": ctx.state.get("acr_provisioner_build_s"),
                   "at_utc": utc()})
    ctx.record("provisioner_images", images)
    run(["dotnet", "build", str(REPO / "src/Atli.Reports.Provisioner"), "-c", "Release", "-nologo", "-v", "q"],
        timeout=900)
    deploy_service(ctx)


def listbug_verify(ctx):
    """The same checks as listbug, with the fixed listing in the CLI and the service."""
    results = ctx.results.get("listing_bug", {})
    cli, rest = Cli(ctx), ctx.rest("rend")
    verify = {"provisioner_commit": ctx.state.get("provisioner_commit"),
              "counts": {"sandboxes": len(list_sandboxes(ctx)), "first_page": len(list_sandboxes_unpaged(ctx)),
                         "records": len(read_records(ctx))}}
    listed = cli("list")
    verify["cli_list"] = {"exit": listed["exit"], "missing_rows": sum(" missing " in l for l in listed["lines"]),
                          "rows": sum(1 for l in listed["lines"] if l.startswith("appa-"))}
    record = read_record(ctx, "appa-b37")
    on_first_page = record["sandboxId"] in {s["id"] for s in list_sandboxes_unpaged(ctx)}
    out = cli("disable", "--tenant", "appa-b37")
    view = rest.sandbox(record["sandboxId"]) or {}
    enabled = cli("enable", "--tenant", "appa-b37")
    after = rest.sandbox(record["sandboxId"]) or {}
    verify["disable"] = {"tenant": "appa-b37", "on_first_page": on_first_page, "exit": out["exit"],
                         "lines": out["lines"], "stopped_reason_after": (view.get("stateDetails") or {}).get("stoppedReason"),
                         "enable_exit": enabled["exit"], "enable_lines": enabled["lines"],
                         "stopped_reason_after_enable": (after.get("stateDetails") or {}).get("stoppedReason")}
    deletes = []
    for tenant in ["appa-b29", "appa-b28", "appa-b27", "appa-b26", "appa-b25", "appa-b24", "appa-b23"]:
        record = read_record(ctx, tenant)
        if not record or record["sandboxId"] in {s["id"] for s in list_sandboxes_unpaged(ctx)}:
            continue
        deleted = client_delete(ctx, tenant, "app-a")
        left = rest.sandbox(record["sandboxId"])
        deletes.append({"tenant": tenant, "on_first_page": False, "delete": deleted.get("status"),
                        "delete_s": deleted.get("s"), "record_after": read_record(ctx, tenant) is not None,
                        "sandbox_after": (left or {}).get("state", "gone")})
        ctx.state.save(repro_deleted=ctx.state.get("repro_deleted", []) + [tenant])
        if left:
            rest.timed("DELETE", f"sandboxes/{record['sandboxId']}")
        break
    verify["gateway_delete_older"] = deletes
    verify["at_utc"] = utc()
    results["after_fix"] = verify
    ctx.record("listing_bug", results)
    log(f"after the fix: {json.dumps({k: verify[k] for k in ['counts', 'cli_list']})}; disable -> "
        f"{verify['disable']['stopped_reason_after']}; delete -> {deletes}")


def listbug(ctx):
    """The provisioner at f03e90e on a group with more than 25 sandboxes: its listing (the data
    plane's first page), `list`, the kill switch, and an application's delete through the gateway."""
    results = ctx.results.get("listing_bug", {})
    cli = Cli(ctx)
    rest = ctx.rest("rend")
    if ctx.options.action == "delete":
        # An application's delete of an older tenant whose renderer is not on the first page.
        attempts = []
        for tenant in ["appa-b30", "appa-b29", "appa-b28"]:
            record = read_record(ctx, tenant)
            if not record or record["sandboxId"] in {s["id"] for s in list_sandboxes_unpaged(ctx)}:
                continue
            deleted = client_delete(ctx, tenant, "app-a")
            after = rest.sandbox(record["sandboxId"])
            entry = {"tenant": tenant, "delete": deleted.get("status"), "delete_s": deleted.get("s"),
                     "record_after": read_record(ctx, tenant) is not None,
                     "sandbox_after": (after or {}).get("state", "gone")}
            if after:
                status, _, _ = rest.timed("DELETE", f"sandboxes/{record['sandboxId']}")
                entry["leaked_sandbox_deleted_by_harness"] = status
            attempts.append(entry)
            ctx.state.save(repro_deleted=ctx.state.get("repro_deleted", []) + [tenant])
            log(f"delete {tenant}: {deleted.get('status')}, record after: {entry['record_after']}, "
                f"sandbox after: {entry['sandbox_after']}")
            break
        results["gateway_delete_older"] = attempts
        ctx.record("listing_bug", results)
        return
    records = read_records(ctx)
    results["counts"] = {"sandboxes": len(list_sandboxes(ctx)), "first_page": len(list_sandboxes_unpaged(ctx)),
                         "records": len(records)}
    listed = cli("list")
    results["cli_list"] = {"exit": listed["exit"], "missing_rows": sum(" missing " in l for l in listed["lines"]),
                           "rows": sum(1 for l in listed["lines"] if l.startswith("appa-"))}
    ctx.record("listing_bug", results)
    # The kill switch, for a tenant whose renderer is not on the first page.
    attempts = []
    for tenant in sorted((t for t in records if t.startswith("appa-b")), reverse=True):
        sandbox = records[tenant]["sandboxId"]
        if sandbox in {s["id"] for s in list_sandboxes_unpaged(ctx)}:
            continue
        out = cli("disable", "--tenant", tenant)
        view = rest.sandbox(sandbox) or {}
        reason = (view.get("stateDetails") or {}).get("stoppedReason")
        attempt = {"tenant": tenant, "exit": out["exit"], "lines": out["lines"], "state_after": view.get("state"),
                   "stopped_reason_after": reason}
        if reason == "Disabled":
            attempt["enable"] = cli("enable", "--tenant", tenant)["exit"]
        attempts.append(attempt)
        log(f"disable {tenant}: exit {out['exit']}, {view.get('state')} ({reason})")
        if reason != "Disabled" or len(attempts) >= 4:
            break
    results["disable_attempts"] = attempts
    ctx.record("listing_bug", results)
    # An application's delete through the gateway, for new tenants, until one's sandbox is left.
    deletes = []
    for index in range(1, 7):
        tenant = f"appa-x{index}"
        created = client_once(ctx, tenant, "app-a")
        record = read_record(ctx, tenant)
        sandbox = record and record.get("sandboxId")
        deleted = client_delete(ctx, tenant, "app-a")
        after = rest.sandbox(sandbox) if sandbox else None
        entry = {"tenant": tenant, "created": created.get("status"), "delete": deleted.get("status"),
                 "delete_s": deleted.get("s"), "record_after": read_record(ctx, tenant) is not None,
                 "sandbox_after": (after or {}).get("state", "gone")}
        if after:
            status, _, _ = rest.timed("DELETE", f"sandboxes/{sandbox}")
            entry["leaked_sandbox_deleted_by_harness"] = status
        deletes.append(entry)
        log(f"delete {tenant}: {deleted.get('status')}, sandbox after: {entry['sandbox_after']}")
        if after:
            break
    results["gateway_deletes"] = deletes
    results["at_utc"] = utc()
    ctx.record("listing_bug", results)


def debug(ctx):
    """An app's revisions and its latest console and system log lines, redacted."""
    app = ctx.options.app
    revisions = prod.arm("GET", f"{app_path(ctx, app)}/revisions", check=False) or {}
    for revision in revisions.get("value", []):
        p = revision.get("properties", {})
        print(redact(json.dumps({k: p.get(k) for k in ["active", "healthState", "runningState", "provisioningState",
                                                         "replicas", "createdTime", "provisioningError"]}
                                | {"name": revision.get("name")})))
    since = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() - ctx.options.minutes * 60))
    for table, column in [("ContainerAppSystemLogs_CL", "Log_s"), ("ContainerAppConsoleLogs_CL", "Log_s")]:
        query = (f"{table} | where ContainerAppName_s == '{app}' | where TimeGenerated >= datetime({since}) "
                 f"| project TimeGenerated, {column}, Reason_s = column_ifexists('Reason_s', '') "
                 f"| order by TimeGenerated desc | take {ctx.options.lines}")
        rows = az_json("monitor", "log-analytics", "query", "-w", ctx.secret["workspace_customer_id"],
                       "--analytics-query", query, "-t", "P1D", check=False) or []
        print(f"== {table}")
        for row in reversed(rows):
            print(redact(f"{row.get('TimeGenerated')} {row.get('Reason_s') or ''} {row.get(column)}"))


# ---------------------------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("command", choices=["setup", "isolation", "leftover", "burst", "same", "quota", "fairness",
                                            "trim", "steady", "delete", "killswitch", "retire", "rollout", "census",
                                            "verify", "cleanup", "service", "gateway", "debug", "rebuild",
                                            "annotate", "listing", "listbug", "provisioner-image"])
    parser.add_argument("action", nargs="?", default="start", help="leftover: start or check")
    parser.add_argument("--work", type=Path, required=True, help="state, secrets, and logs (mode 0700)")
    parser.add_argument("--output", type=Path, required=True, help="results JSON (redacted)")
    parser.add_argument("--location", default="eastus2")
    parser.add_argument("--aca", type=Path, help="the aca binary, when it is not on PATH")
    parser.add_argument("--duration", type=int, default=600)
    parser.add_argument("--timeout", type=int, default=1500)
    parser.add_argument("--extra", nargs="*", default=[], help="verify: more files to search")
    parser.add_argument("--app", default=GATEWAY, help="debug: the app")
    parser.add_argument("--minutes", type=int, default=15, help="debug: how far back")
    parser.add_argument("--lines", type=int, default=40, help="debug: lines per table")
    options = parser.parse_args()
    if options.aca:
        os.environ["PATH"] = f"{options.aca.resolve().parent}{os.pathsep}{os.environ['PATH']}"
    for tool in ["az", "aca", "dotnet", "git", "openssl", "curl"]:
        if not shutil.which(tool):
            sys.exit(f"Missing required tool: {tool}")
    ctx = Ctx(options)
    if options.command == "verify":
        ctx.record("verify", verify(ctx, options.extra))
    elif options.command == "annotate":
        # Merges a JSON object from --extra's file into the results under its keys (one level deep).
        for key, value in json.loads(Path(options.extra[0]).read_text()).items():
            if isinstance(value, dict) and isinstance(ctx.results.get(key), dict):
                ctx.record(key, {**ctx.results[key], **value})
            else:
                ctx.record(key, value)
    elif options.command == "census":
        print(redact(json.dumps(census_brief(census(ctx)), indent=2)))
    elif options.command == "service":
        deploy_service(ctx)
    elif options.command == "provisioner-image":
        provisioner_image(ctx)
    elif options.command == "listbug" and options.action == "verify":
        listbug_verify(ctx)
    elif options.command == "rollout" and options.action == "finish":
        rollout_finish(ctx)
    elif options.command == "gateway":
        deploy_gateway(ctx)
    else:
        globals()[options.command](ctx)


if __name__ == "__main__":
    main()
