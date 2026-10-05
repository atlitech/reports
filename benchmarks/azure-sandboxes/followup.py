#!/usr/bin/env python3
"""Follow-up experiments for the server image as a renderer on Azure Container Apps Sandboxes.

Answers the questions run.py left open, each as a subcommand, in a resource group of its own:

  setup    Creates the resource group, a renderer sandbox group, a "tester" sandbox group, and the
           server's disk image (built remotely from this commit's server Dockerfile, as run.py does).
  ingress  Port access control on a small HTTP server sandbox: an Entra ID-gated port (anonymous
           requests, bearer tokens of several audiences, the browser sign-in redirect), the port's
           source-IP allow-list, OnDemand activation (a request resumes a stopped sandbox), and
           whether requests the port refuses still wake it.
  role     A custom role with only the data actions sandboxes/read and sandboxes/resume/action,
           assignable only in the resource group and assigned on the renderer group to a
           user-assigned managed identity. The tester group runs with that identity;
           role-probe.py, inside a tester sandbox, calls the renderer group's data plane with the
           identity's token and records each call's status. The role assignment, role definition,
           and identity are deleted at the end.
  dns      DNS from a renderer with deny-by-default egress (dns-probe.pl): which resolver, which
           record types, and whether egress rules change it.
  load     Concurrent load on one renderer: renderers at 2 vCPU / 4 GiB (L) and 1 vCPU / 2 GiB (M)
           (--sizes adds S), driven by load.py from a client sandbox in the region at concurrency
           1, 2, 4, and 8 for the invoice and the 49-page report, with the VM's CPU, memory, and
           OOM kills sampled during each cell; then the planned per-size limits of RendererSize;
           with --recommended, one conversion per vCPU; and, on the last size's planned renderer,
           OnDemand activation and the disable/enable kill switch. A rerun repeats only failed or
           missing cells.
  ondemand Waking a stopped renderer, from a client sandbox in the region (wake.py): the port's
           OnDemand activation (an invoice, the 49-page report, and a burst of six requests during
           the wake) against Manual activation plus an explicit resume call, and auto-suspend after
           OnDemand wakes.
  cleanup  Deletes the sandbox groups, the resource group, and any role assignment, role
           definition, or identity recorded in the state file.

State (resource names and IDs, never secrets) is kept in --state so the subcommands can run one at
a time; results are merged into --output. Requirements: az (logged in, with rights to create a
resource group, a custom role, and a role assignment), aca (https://aka.ms/aca-cli-install), git,
and openssl. The renderers' API key is generated per run and never printed.

  benchmarks/azure-sandboxes/followup.py setup|ingress|role|dns|load|ondemand|cleanup \
      [--state state.json] [--output results.json] [--location eastus2]

The 2026-10-04 run (benchmarks/results/2026-10-04-5d557b4-azure-sandboxes-followup-amd64.md) used
the latest aca, 1.0.0-preview.4. The virtual-network DNS test there was run by hand; the results
file lists its commands.
"""
# The Manual activation and resume-role probes below preserve the original Azure platform
# experiments. The current gateway uses OnDemand ports and does not need a sandbox resume role.
import argparse
import base64
import json
import re
import shutil
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import run as spike  # noqa: E402  run.py's helpers: Aca, http, log, write_requests, build_context

REPO = spike.REPO
API_VERSION = spike.API_VERSION
SIZES = {"L": ("2000m", "4096Mi", 4), "M": ("1000m", "2048Mi", 2), "S": ("500m", "1024Mi", 1)}
# The sign-in app the port proxy redirects browsers to (observed on 2026-10-04).
PORT_SIGN_IN_CLIENT = "409cf302-c83f-43c3-94eb-ca581ab18c6d"
log = spike.log


# ---------------------------------------------------------------------------------------------
# State, Azure CLI, and the data plane
# ---------------------------------------------------------------------------------------------

class State(dict):
    def __init__(self, path):
        super().__init__(json.loads(path.read_text()) if path.exists() else {})
        self.path = path

    def save(self, **values):
        self.update(values)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps(self, indent=2) + "\n")


def az(*args, check=True, timeout=1800):
    return spike.run(["az", *args], check=check, timeout=timeout)


def az_json(*args):
    return json.loads(az(*args, "-o", "json").stdout or "null")


def token_for(resource):
    return az("account", "get-access-token", "--resource", resource, "--query", "accessToken", "-o", "tsv").stdout.strip()


class Rest(spike.Rest):
    """run.py's data-plane client, returning (status, body, seconds) instead of raising."""

    def __init__(self, subscription, resource_group, group, region):
        super().__init__(subscription, resource_group, group, region)

    def request(self, method, path, body=None):
        url = f"{self.base}/{path}" if path else self.base
        url += ("&" if "?" in url else "?") + f"api-version={API_VERSION}"
        request = urllib.request.Request(url, method=method,
                                         data=None if body is None else json.dumps(body).encode(),
                                         headers={"Authorization": f"Bearer {self.token}",
                                                  "Content-Type": "application/json"})
        started = time.monotonic()
        try:
            with urllib.request.urlopen(request, timeout=600) as response:
                status, data = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, data = error.code, error.read()
        try:
            parsed = json.loads(data or b"{}")
        except ValueError:
            parsed = {"text": data[:300].decode(errors="replace")}
        return status, parsed, time.monotonic() - started

    def state_of(self, sandbox):
        return self.request("GET", f"sandboxes/{sandbox}")[1].get("state")

    def wait(self, sandbox, state, limit=600):
        started = time.monotonic()
        while self.state_of(sandbox) != state:
            if time.monotonic() - started > limit:
                raise RuntimeError(f"{sandbox} did not reach {state} within {limit} s")
            time.sleep(0.5)
        return time.monotonic() - started

    def renderer(self, disk, environment, size, label):
        """A renderer as the provisioner would create it: the server image's entrypoint, deny-by-default
        egress, no auto-suspend, and the given environment."""
        cpu, memory, _ = SIZES[size]
        body = {
            "sourcesRef": {"diskImage": {"id": disk, "isPublic": False}},
            "resources": {"cpu": cpu, "memory": memory},
            "egressPolicy": {"defaultAction": "Deny"},
            "entrypoint": spike.ENTRYPOINT,
            "environment": environment,
            "labels": {"role": label},
            "lifecycle": {"autoSuspendPolicy": {"enabled": False, "interval": 0}},
        }
        status, view, seconds = self.request("PUT", "sandboxes", body)
        if status >= 300:
            raise RuntimeError(f"renderer create: {status} {view}")
        return view["id"], seconds

    def set_port(self, sandbox, port):
        """Replaces one port's settings. The bulk PUT of 2026-02-01-preview wants whole port views,
        so remove the port, then add it again."""
        self.request("POST", f"sandboxes/{sandbox}/ports/remove", {"port": port["port"]})
        status, view, _ = self.request("POST", f"sandboxes/{sandbox}/ports/add", port)
        if status >= 300:
            raise RuntimeError(f"ports/add: {status} {view}")
        ports = view.get("ports", []) if isinstance(view, dict) else view
        return next(p for p in ports if p["port"] == port["port"])


class Context:
    def __init__(self, options):
        self.options = options
        self.state = State(options.state)
        self.results = json.loads(options.output.read_text()) if options.output.exists() else {}
        account = az_json("account", "show")
        self.subscription = account["id"]
        self.region = options.location

    def record(self, key, value):
        self.results[key] = value
        self.options.output.parent.mkdir(parents=True, exist_ok=True)
        self.options.output.write_text(json.dumps(self.results, indent=2) + "\n")

    @property
    def resource_group(self):
        return self.state["resource_group"]

    def aca(self, group):
        return spike.Aca(self.subscription, self.resource_group, group, self.region)

    def rest(self, group):
        return Rest(self.subscription, self.resource_group, group, self.region)

    def group_scope(self, group):
        return (f"/subscriptions/{self.subscription}/resourceGroups/{self.resource_group}"
                f"/providers/Microsoft.App/sandboxGroups/{group}")


def credentials(work):
    """A fresh API key for the renderers, as scripts/create-reports-api-key.sh writes it, and the
    renderer environment of src/Atli.Reports.Hosting/Renderers/RendererServerEnvironment.cs."""
    spike.run(["bash", str(REPO / "scripts/create-reports-api-key.sh"), str(work / "credentials"), "gateway"])
    server = dict(line.split("=", 1) for line in (work / "credentials/server.env").read_text().split())
    key = re.search(r"^ReportsClient__ApiKey=(.+)$", (work / "credentials/client.env").read_text(), re.M).group(1)
    (work / "api-key").write_text(key)
    (work / "api-key").chmod(0o600)

    def environment(conversions, per_caller):
        return {
            "ReportsServer__Authentication__Mode": "ApiKey",
            "ReportsServer__Authentication__ApiKeys__0__Id": server["ReportsServer__Authentication__ApiKeys__0__Id"],
            "ReportsServer__Authentication__ApiKeys__0__Hash": server["ReportsServer__Authentication__ApiKeys__0__Hash"],
            "ReportsServer__Authentication__ApiKeys__0__CallerId": "gateway",
            "ReportsServer__Authentication__ApiKeys__0__Permissions__0": "reports.convert",
            "ReportsServer__Limits__MaxConcurrentRequestsPerCaller": str(per_caller),
            "ReportsEngine__Concurrency__MaxConcurrentConversions": str(conversions),
            "ReportsEngine__Network__Mode": "Disabled",
        }

    return environment


def wait_ready(url, limit=180):
    return spike.wait_ready(url, time.monotonic(), limit)


# ---------------------------------------------------------------------------------------------
# setup
# ---------------------------------------------------------------------------------------------

def setup(ctx):
    stamp = time.strftime("%Y%m%d%H%M%S", time.gmtime())
    commit = spike.run(["git", "-C", str(REPO), "rev-parse", "--short", "HEAD"]).stdout.strip()
    if "resource_group" not in ctx.state:
        resource_group = f"rg-atli-reports-followup-{stamp}"
        log(f"Resource group {resource_group}")
        az("group", "create", "--name", resource_group, "--location", ctx.region,
           "--tags", "purpose=atli-reports-sandboxes-followup", f"commit={commit}", "-o", "none")
        ctx.state.save(resource_group=resource_group, stamp=stamp,
                       renderer_group=f"reports-followup-{stamp}", tester_group=f"reports-tester-{stamp}")
    propagation = {}
    for key in ["renderer_group", "tester_group"]:
        group = ctx.state[key]
        aca = ctx.aca(group)
        if aca("sandbox", "list", "-o", "json", check=False).returncode != 0:
            aca("sandboxgroup", "create", "--name", group, "--location", ctx.region, check=False)
        started = time.monotonic()
        # The group's create grants the caller the Data Owner role; until it propagates, 403.
        while aca("sandbox", "list", "-o", "json", check=False).returncode != 0:
            if time.monotonic() - started > 900:
                raise RuntimeError("The data-plane role did not propagate within 15 minutes.")
            time.sleep(15)
        propagation[group] = round(time.monotonic() - started, 1)
    if "disk" not in ctx.state:
        work = Path(tempfile.mkdtemp(prefix="atli-followup-"))
        try:
            context = spike.build_context(work)
            aca = ctx.aca(ctx.state["renderer_group"])
            started = time.monotonic()
            aca("sandboxgroup", "disk", "create", "--source", str(context), "--name", f"reports-server-{commit}",
                "--wait-timeout", "3000", timeout=3600)
            build_s = round(time.monotonic() - started, 1)
            disks = json.loads(aca("sandboxgroup", "disk", "list", "-o", "json").stdout)
            disk = next(d["id"] for d in disks if d.get("name") == f"reports-server-{commit}")
            ctx.state.save(disk=disk, disk_build_s=build_s, commit=commit)
        finally:
            shutil.rmtree(work, ignore_errors=True)
    ctx.record("setup", {"commit": ctx.state["commit"], "aca_cli": spike.run(["aca", "--version"]).stdout.strip(),
                         "location": ctx.region, "role_propagation_s": propagation,
                         "disk_build_s": ctx.state.get("disk_build_s")})


# ---------------------------------------------------------------------------------------------
# ingress
# ---------------------------------------------------------------------------------------------

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def get_no_redirect(url, headers):
    opener = urllib.request.build_opener(NoRedirect)
    try:
        with opener.open(urllib.request.Request(url, headers=headers), timeout=60) as response:
            return response.status, response.headers, response.read()[:200]
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read()[:200]


def ingress(ctx, laptop_ip):
    group = ctx.state["renderer_group"]
    aca, rest = ctx.aca(group), ctx.rest(group)
    me = az("ad", "signed-in-user", "show", "--query", "id", "-o", "tsv").stdout.strip()
    results = {}
    sandbox = ctx.state.get("ingress_sandbox")
    if not sandbox:
        # Public disks ignore the entrypoint; start the server with exec instead.
        sandbox, _ = aca.create("--disk", "python-3.12", "--cpu", "500m", "--memory", "1024Mi",
                                "--label", "role=ingress-test")
        ctx.state.save(ingress_sandbox=sandbox)
    if rest.state_of(sandbox) != "Running":
        rest.request("POST", f"sandboxes/{sandbox}/resume", {})
        rest.wait(sandbox, "Running")
    aca.exec(sandbox, "echo ok > /tmp/index.html; (curl -s -m 2 http://127.0.0.1:8080/ >/dev/null) || "
                      "(setsid nohup python3 -m http.server 8080 --directory /tmp >/dev/null 2>&1 </dev/null & sleep 1)")
    url = f"https://{sandbox}--8080.{ctx.region}.adcproxy.io"

    # 1. Entra ID-gated port, limited to the signed-in user's object ID.
    port = rest.set_port(sandbox, {"port": 8080, "auth": {"entraId": {"enabled": True, "objectIds": [me]}}})
    results["entra_port_view"] = {k: v for k, v in port.items() if k != "url"}
    status, headers, body = get_no_redirect(url + "/", {})
    results["entra_anonymous"] = {"status": status, "www_authenticate": headers.get("WWW-Authenticate"),
                                  "body": body.decode(errors="replace")}
    status, headers, _ = get_no_redirect(url + "/", {"Accept": "text/html"})
    hop = headers.get("Location", "")
    status2, headers2, _ = get_no_redirect(hop, {"Accept": "text/html"}) if hop else (None, {}, b"")
    entra = urllib.parse.urlparse(headers2.get("Location", ""))
    query = urllib.parse.parse_qs(entra.query)
    results["entra_browser"] = {
        "first": f"{status} -> {urllib.parse.urlparse(hop).netloc}{urllib.parse.urlparse(hop).path}",
        "second": f"{status2} -> {entra.netloc}{entra.path}",
        "client_id": query.get("client_id", [None])[0], "scope": query.get("scope", [None])[0],
        "redirect_uri": query.get("redirect_uri", [None])[0],
        "cookies": sorted({c.split("=", 1)[0] for c in (getattr(headers2, "get_all", lambda _: [])("Set-Cookie") or [])}),
    }
    bearer = {}
    for resource in ["https://dynamicsessions.io", "https://management.azuredevcompute.io",
                     "https://management.azure.com/", "https://graph.microsoft.com", PORT_SIGN_IN_CLIENT,
                     f"api://{PORT_SIGN_IN_CLIENT}", f"https://{ctx.region}.adcproxy.io"]:
        result = az("account", "get-access-token", "--resource", resource, "--query", "accessToken", "-o", "tsv",
                    check=False)
        if result.returncode != 0:
            bearer[resource] = "no token: " + result.stderr.strip().splitlines()[-1][:160]
            continue
        token = result.stdout.strip()
        header = json.loads(base64.urlsafe_b64decode(token.split(".")[0] + "=" * (-len(token.split(".")[0]) % 4)))
        status, _, _ = get_no_redirect(url + "/", {"Authorization": f"Bearer {token}"})
        bearer[resource] = f"{status} ({'encrypted JWE' if token.count('.') == 4 else header.get('alg')})"
    results["entra_bearer"] = bearer

    # 2. Source-IP allow-list.
    acl = {}
    for name, cidr in [("allowed", f"{laptop_ip}/32"), ("not_allowed", "203.0.113.0/24")]:
        rest.set_port(sandbox, {"port": 8080, "auth": {"anonymous": True}, "ipAccessControl": {
            "defaultAction": "Deny", "rules": [{"name": "gateway", "action": "Allow", "priority": 10,
                                                "sourceCidrs": [cidr]}]}})
        time.sleep(3)
        status, _, body, _ = spike_http(url + "/")
        acl[name] = f"{status} {body.decode(errors='replace')[:100]}"
    results["ip_access_control"] = acl

    # 3. OnDemand activation, and whether refused requests wake the sandbox.
    rest.set_port(sandbox, {"port": 8080, "auth": {"anonymous": True}, "activationMode": "OnDemand"})
    cycles = []
    for _ in range(3):
        rest.request("POST", f"sandboxes/{sandbox}/stop", {})
        rest.wait(sandbox, "Stopped")
        time.sleep(3)
        status, _, body, seconds = spike_http(url + "/")
        cycles.append({"status": status, "first_request_from_laptop_s": round(seconds, 2),
                       "state_after": rest.state_of(sandbox)})
    results["on_demand"] = cycles
    wake = {}
    for name, port in {
        "ip_denied": {"port": 8080, "auth": {"anonymous": True}, "activationMode": "OnDemand",
                      "ipAccessControl": {"defaultAction": "Deny", "rules": [
                          {"name": "gateway", "action": "Allow", "priority": 10, "sourceCidrs": ["203.0.113.0/24"]}]}},
        "entra_unauthenticated": {"port": 8080, "auth": {"entraId": {"enabled": True, "objectIds": [me]}},
                                  "activationMode": "OnDemand"},
    }.items():
        if rest.state_of(sandbox) != "Running":
            rest.request("POST", f"sandboxes/{sandbox}/resume", {})
            rest.wait(sandbox, "Running")
        rest.set_port(sandbox, port)
        rest.request("POST", f"sandboxes/{sandbox}/stop", {})
        rest.wait(sandbox, "Stopped")
        time.sleep(3)
        status, _, _, _ = spike_http(url + "/")
        time.sleep(10)
        wake[name] = {"status": status, "state_10s_later": rest.state_of(sandbox)}
    results["refused_requests_wake"] = wake
    ctx.record("ingress", results)


def spike_http(url, data=None, headers=None):
    status, seconds, body = spike.http(url, data=data, headers=headers)
    return status, None, body, seconds


# ---------------------------------------------------------------------------------------------
# role
# ---------------------------------------------------------------------------------------------

def role(ctx):
    renderer_group, tester_group = ctx.state["renderer_group"], ctx.state["tester_group"]
    stamp, rg = ctx.state["stamp"], ctx.resource_group
    rg_scope = f"/subscriptions/{ctx.subscription}/resourceGroups/{rg}"
    results = {}
    if "identity" not in ctx.state:
        identity = az_json("identity", "create", "--name", f"id-reports-resume-{stamp}", "--resource-group", rg,
                           "--location", ctx.region, "--tags", "purpose=atli-reports-sandboxes-followup")
        ctx.state.save(identity={k: identity[k] for k in ["id", "principalId", "clientId"]})
    identity = ctx.state["identity"]
    if "role_definition" not in ctx.state:
        definition = {
            "Name": f"Atli Reports Sandbox Resumer (followup {stamp})",
            "IsCustom": True,
            "Description": "Temporary test role: read and resume sandboxes only.",
            "Actions": [], "NotActions": [],
            "DataActions": ["Microsoft.App/sandboxGroups/sandboxes/read",
                            "Microsoft.App/sandboxGroups/sandboxes/resume/action"],
            "NotDataActions": [],
            "AssignableScopes": [rg_scope],
        }
        work = Path(tempfile.mkdtemp(prefix="atli-followup-"))
        (work / "role.json").write_text(json.dumps(definition))
        created = az_json("role", "definition", "create", "--role-definition", f"@{work / 'role.json'}")
        shutil.rmtree(work, ignore_errors=True)
        ctx.state.save(role_definition=created["name"])
    if "role_assignment" not in ctx.state:
        for attempt in range(20):  # a new role definition can take a while to be assignable
            result = az("role", "assignment", "create", "--assignee-object-id", identity["principalId"],
                        "--assignee-principal-type", "ServicePrincipal", "--role", ctx.state["role_definition"],
                        "--scope", ctx.group_scope(renderer_group), "--query", "id", "-o", "tsv", check=False)
            if result.returncode == 0:
                break
            time.sleep(15)
        ctx.state.save(role_assignment=result.stdout.strip(), role_assigned_utc=time.strftime("%H:%M:%S", time.gmtime()))
    tester_aca = ctx.aca(tester_group)
    tester_aca("sandboxgroup", "identity", "assign", "--group", tester_group, "--user-assigned", identity["id"])
    if "tester_sandbox" not in ctx.state:
        tester, _ = tester_aca.create("--disk", "python-3.12", "--cpu", "500m", "--memory", "1024Mi",
                                      "--egress-default", "Deny",
                                      "--egress-rule", f"management.{ctx.region}.azuredevcompute.io:Allow",
                                      "--egress-rule", "*.adcproxy.io:Allow", "--label", "role=tester")
        ctx.state.save(tester_sandbox=tester)
    tester = ctx.state["tester_sandbox"]
    tester_aca.write(tester, "/root/role-probe.py", HERE / "role-probe.py")

    # The target: a renderer as the provisioner creates it, at size S.
    rest = ctx.rest(renderer_group)
    work = Path(tempfile.mkdtemp(prefix="atli-followup-"))
    try:
        environment = credentials(work)
        target, _ = rest.renderer(ctx.state["disk"], environment(1, 1), "S", "role-target")
        rest.set_port(target, {"port": 8080, "auth": {"anonymous": True}})
        wait_ready(f"https://{target}--8080.{ctx.region}.adcproxy.io")
    finally:
        shutil.rmtree(work, ignore_errors=True)
    base = (f"https://management.{ctx.region}.azuredevcompute.io/subscriptions/{ctx.subscription}"
            f"/resourceGroups/{rg}/sandboxGroups/{renderer_group}")

    def probe(phase, *extra):
        output = tester_aca.exec(tester, f"python3 /root/role-probe.py {phase} {base} {target} "
                                         f"{identity['clientId']} {' '.join(extra)}", check=False, timeout=1800)
        lines = [json.loads(line) for line in output.stdout.splitlines() if line.startswith("{")]
        for line in lines:
            log(f"  {line}")
        return lines

    log("Calls the role must refuse, while the renderer runs")
    started = time.monotonic()
    results["denied_phase"] = probe("denied")
    results["denied_phase_s"] = round(time.monotonic() - started, 1)
    results["target_state_after_denied_phase"] = rest.state_of(target)
    log("Stop the renderer as the owner, then resume it with the role")
    rest.request("POST", f"sandboxes/{target}/stop", {})
    rest.wait(target, "Stopped")
    results["resume_phase"] = probe("resume")
    results["target_state_after_resume"] = rest.state_of(target)
    log("Entra ID-gated port allowing only the identity")
    rest.set_port(target, {"port": 8080, "auth": {"entraId": {"enabled": True, "objectIds": [identity["principalId"]]}}})
    results["port_phase"] = probe("port", f"https://{target}--8080.{ctx.region}.adcproxy.io")
    rest.request("DELETE", f"sandboxes/{target}")
    ctx.record("role", results)
    role_cleanup(ctx)


def role_cleanup(ctx):
    """Deletes the role assignment, the role definition, and the identity (and detaches it)."""
    if ctx.state.get("role_assignment"):
        az("role", "assignment", "delete", "--ids", ctx.state["role_assignment"], check=False)
        ctx.state.save(role_assignment="")
    if ctx.state.get("role_definition"):
        az("role", "definition", "delete", "--name", ctx.state["role_definition"],
           "--scope", f"/subscriptions/{ctx.subscription}/resourceGroups/{ctx.resource_group}", check=False)
        ctx.state.save(role_definition="")
    if ctx.state.get("identity"):
        # aca 1.0.0-preview.4's `sandboxgroup identity remove --user-assigned` answers 400 ("identity
        # ids are only supported for 'UserAssigned' identity type"); ARM clears it.
        az("rest", "--method", "patch", "--url",
           f"https://management.azure.com{ctx.group_scope(ctx.state['tester_group'])}?api-version={API_VERSION}",
           "--body", '{"identity":{"type":"None"}}', "-o", "none", check=False)
        az("identity", "delete", "--ids", ctx.state["identity"]["id"], check=False)
        ctx.state.save(identity=None)


# ---------------------------------------------------------------------------------------------
# dns
# ---------------------------------------------------------------------------------------------

def dns(ctx):
    group = ctx.state["renderer_group"]
    aca, rest = ctx.aca(group), ctx.rest(group)
    work = Path(tempfile.mkdtemp(prefix="atli-followup-"))
    results = {}
    try:
        environment = credentials(work)
        renderer, _ = rest.renderer(ctx.state["disk"], environment(1, 1), "S", "dns")
        rest.wait(renderer, "Running")
        aca.write(renderer, "/tmp/spike/dns-probe.pl", HERE / "dns-probe.pl")
        aca.write(renderer, "/tmp/spike/network-probe.sh", HERE / "network-probe.sh")
        script = "cat /etc/resolv.conf | grep -v '^#'; perl /tmp/spike/dns-probe.pl"

        def probe():
            output = aca.exec(renderer, script, check=False).stdout
            return [line for line in output.splitlines() if line.strip()]

        results["deny_default"] = probe()
        # An explicit deny rule for the probed names, then an allow rule: does either change DNS?
        for name, rules in [("deny_rules", ["*.example.com:Deny", "example.com:Deny", "*.microsoft.com:Deny"]),
                            ("allow_rule", ["example.com:Allow"])]:
            args = ["sandbox", "egress", "set", "--id", renderer, "--default", "Deny"]
            for rule in rules:
                args += ["--rule", rule]
            aca(*args)
            time.sleep(5)
            results[name] = {"rules": rules, "probe": probe(),
                             "network": aca.exec(renderer, "bash /tmp/spike/network-probe.sh", check=False).stdout.split()}
        rest.request("DELETE", f"sandboxes/{renderer}")
    finally:
        shutil.rmtree(work, ignore_errors=True)
    ctx.record("dns", results)


# ---------------------------------------------------------------------------------------------
# load
# ---------------------------------------------------------------------------------------------

SAMPLER = (
    # The VM's peak memory (MemTotal - MemAvailable), its CPU busy share, and OOM kills over <seconds>.
    'D={seconds}; mem() {{ awk "/MemTotal/{{t=\\$2}}/MemAvailable/{{a=\\$2}}END{{print int((t-a)/1024)}}" /proc/meminfo; }}; '
    'cpu() {{ awk "/^cpu /{{print \\$2+\\$3+\\$4+\\$7+\\$8+\\$9, \\$5+\\$6}}" /proc/stat; }}; '
    'oom() {{ awk "/oom_kill /{{print \\$2}}" /proc/vmstat; }}; '
    'o0=$(oom); set -- $(cpu); b0=$1; i0=$2; base=$(mem); peak=$base; end=$(( $(date +%s) + D )); '
    'while [ $(date +%s) -lt $end ]; do m=$(mem); [ $m -gt $peak ] && peak=$m; sleep 0.25; done; '
    'set -- $(cpu); echo "before_mib=$base peak_mib=$peak cpu_busy_pct=$(( 100 * ($1 - b0) / (($1 - b0) + ($2 - i0)) )) '
    'oom_kills=$(( $(oom) - o0 ))"'
)


def load(ctx):
    group = ctx.state["renderer_group"]
    aca, rest = ctx.aca(group), ctx.rest(group)
    options = ctx.options
    work = Path(tempfile.mkdtemp(prefix="atli-followup-"))
    results = ctx.results.get("load", {})
    try:
        environment = credentials(work)
        spike.write_requests(work, work / "credentials")
        if "client_sandbox" not in ctx.state:
            client, _ = aca.create("--disk", "python-3.12", "--cpu", "2000m", "--memory", "4096Mi",
                                   "--egress-default", "Deny", "--egress-rule", "*.adcproxy.io:Allow",
                                   "--label", "role=load-client")
            ctx.state.save(client_sandbox=client)
        client = ctx.state["client_sandbox"]
        rest.request("POST", f"sandboxes/{client}/lifecycle", {"autoSuspendPolicy": {"enabled": False, "interval": 0}})
        if rest.state_of(client) != "Running":
            rest.request("POST", f"sandboxes/{client}/resume", {})
            rest.wait(client, "Running")
        aca.write(client, "/root/spike/load.py", HERE / "load.py")
        aca.write(client, "/root/spike/api-key", work / "api-key")
        aca.write(client, "/root/spike/client.curl", work / "credentials/client.curl")
        for fixture in ["invoice", "long-table"]:
            aca.write(client, f"/root/spike/{fixture}.json", work / f"{fixture}.json")
        results["client"] = aca.exec(client, "nproc; python3 --version").stdout.split()

        configs = []
        for size in options.sizes:
            planned = SIZES[size][2]
            # The sweep: client concurrency equals the conversions the browser runs at once.
            configs.append((f"sweep-{size}", size, environment(8, 16),
                            [(f, c, False) for f in ["invoice", "long-table"] for c in options.concurrency]))
            # The planned defaults, exactly as RendererServerEnvironment sets them: the per-caller
            # in-flight limit equals the engine's, so excess requests get 429 at once.
            configs.append((f"planned-{size}", size, environment(planned, planned),
                            [(f, c, True) for f in ["invoice", "long-table"] for c in sorted({planned, 8})]))
            if options.recommended:
                # One conversion per vCPU (at least one), and one more request per slot waiting in
                # the engine's queue rather than refused with 429.
                conversions = max(1, int(SIZES[size][0][:-1]) // 1000)
                configs.append((f"recommended-{size}", size, environment(conversions, 2 * conversions),
                                [(f, c, True) for f in ["invoice", "long-table"]
                                 for c in sorted({2 * conversions, 8})]))
        for name, size, env, cells in configs:
            # A rerun repeats only the cells that failed or are missing (all of them with --redo).
            previous = {} if options.redo else results.get(name, {})
            done = {(c["fixture"], c["concurrency"]): c for c in previous.get("cells", [])
                    if "error" not in c and "concurrency" in c}
            wants_on_demand = (name == f"planned-{options.sizes[-1]}" and not options.skip_on_demand
                               and "cycles" not in previous.get("on_demand", {}))
            todo = [cell for cell in cells if (cell[0], cell[1]) not in done]
            if not todo and not wants_on_demand:
                continue
            log(f"{name}: {SIZES[size][:2]}, {len(todo)} cells")
            renderer, _ = rest.renderer(ctx.state["disk"], env, size, name)
            url = rest.set_port(renderer, {"port": 8080, "auth": {"anonymous": True}})["url"]
            ready_s = wait_ready(url)
            entry = {"size": size, "cpu": SIZES[size][0], "memory": SIZES[size][1],
                     "MaxConcurrentConversions": int(env["ReportsEngine__Concurrency__MaxConcurrentConversions"]),
                     "MaxConcurrentRequestsPerCaller": int(env["ReportsServer__Limits__MaxConcurrentRequestsPerCaller"]),
                     "ready_after_create_s": round(ready_s, 2), "cells": []}
            if previous.get("on_demand") and not wants_on_demand:
                entry["on_demand"] = previous["on_demand"]
            for fixture, concurrency, backoff in cells:
                if (fixture, concurrency) in done:
                    entry["cells"].append(done[(fixture, concurrency)])
                    continue
                duration = options.invoice_seconds if fixture == "invoice" else options.long_seconds
                line = load_cell(aca, client, renderer, url, fixture, concurrency, duration, backoff)
                line["ready_after"] = spike.http(f"{url}/health/ready")[0]
                log(f"  {fixture} c={concurrency}: {line.get('pdfs_per_s')} PDF/s p50={line.get('p50_s')} "
                    f"p95={line.get('p95_s')} statuses={line.get('statuses')} vm={line.get('vm')} "
                    f"{line.get('error', '')}")
                entry["cells"].append(line)
            if wants_on_demand:
                try:
                    entry["on_demand"] = on_demand(ctx, rest, aca, client, renderer, url)
                except RuntimeError as error:
                    entry["on_demand"] = {"error": str(error)[:500]}
            results[name] = entry
            ctx.record("load", results)
            rest.request("DELETE", f"sandboxes/{renderer}")
    finally:
        shutil.rmtree(work, ignore_errors=True)
    ctx.record("load", results)


def load_cell(aca, client, renderer, url, fixture, concurrency, duration, backoff):
    """One cell: load.py in the client and the sampler in the renderer, both started in the
    background (an aca exec request ends after about a minute), then polled for their output."""
    label = f"{fixture}-c{concurrency}"
    aca.exec(renderer, f"rm -f /tmp/sample-{label}; setsid nohup sh -c '{SAMPLER.format(seconds=int(duration) + 5)}' "
                       f"> /tmp/sample-{label} 2>&1 < /dev/null &")
    aca.exec(client, (
        f"cd /root/spike && rm -f out-{label}.json && setsid nohup python3 load.py --url {url} --key-file api-key "
        f"--body {fixture}.json --concurrency {concurrency} --duration {duration} --label {label}"
        + (" --honor-retry-after" if backoff else "") + f" > out-{label}.json 2>&1 < /dev/null &"))
    deadline = time.monotonic() + duration + 600
    line = {"error": "no result within the deadline"}
    while time.monotonic() < deadline:
        time.sleep(5)
        text = aca.exec(client, f"cat /root/spike/out-{label}.json", check=False).stdout
        found = next((l for l in text.splitlines() if l.startswith("{")), None)
        if found:
            line = json.loads(found)
            break
        if "Traceback" in text:
            line = {"error": text[-300:]}
            break
    for _ in range(12):  # the sampler runs for the cell's duration plus 5 s from its own start
        sample = aca.exec(renderer, f"cat /tmp/sample-{label}", check=False).stdout
        if "peak_mib" in sample:
            break
        time.sleep(5)
    line.update({"fixture": fixture, "concurrency": concurrency,
                 "vm": dict(pair.split("=", 1) for pair in sample.split() if "=" in pair)})
    return line


def on_demand(ctx, rest, aca, client, renderer, url, cycles=3):
    """OnDemand activation of a stopped renderer, timed from the client sandbox: no resume call."""
    rest.set_port(renderer, {"port": 8080, "auth": {"anonymous": True}, "activationMode": "OnDemand"})
    curl = (f"cd /root/spike && curl -s -o /dev/null -m 120 --config client.curl -H 'Content-Type: application/json' "
            f"--data-binary @invoice.json -w '%{{http_code}} %{{time_total}}\\n' {url}/convert")
    results = []
    for _ in range(cycles):
        rest.request("POST", f"sandboxes/{renderer}/stop", {})
        rest.wait(renderer, "Stopped")
        time.sleep(5)
        first = aca.exec(client, curl, check=False).stdout.split()
        second = aca.exec(client, curl, check=False).stdout.split()
        results.append({"first_invoice": first, "second_invoice": second, "state_after": rest.state_of(renderer)})
        log(f"  OnDemand: first {first} second {second}")
    # A disabled sandbox must stay stopped: the kill switch for a compromised renderer.
    status, body, _ = rest.request("POST", f"sandboxes/{renderer}/disable", {})
    rest.wait(renderer, "Stopped", limit=120)
    time.sleep(5)
    request_while_disabled = aca.exec(client, curl, check=False).stdout.split()
    view = rest.request("GET", f"sandboxes/{renderer}")[1]
    resume_while_disabled = rest.request("POST", f"sandboxes/{renderer}/resume", {})[:2]
    enable = rest.request("POST", f"sandboxes/{renderer}/enable", {})[0]
    return {"cycles": results, "disable_call": status,
            "request_while_disabled": request_while_disabled, "state_while_disabled": view.get("state"),
            "state_details_while_disabled": view.get("stateDetails"),
            "resume_while_disabled": [resume_while_disabled[0], str(resume_while_disabled[1])[:200]],
            "enable_call": enable}


# ---------------------------------------------------------------------------------------------
# ondemand
# ---------------------------------------------------------------------------------------------

def ondemand(ctx):
    """Waking a stopped renderer: OnDemand activation against Manual plus an explicit resume call,
    measured from a client sandbox in the region (wake.py), and auto-suspend after a wake."""
    group = ctx.state["renderer_group"]
    aca, rest = ctx.aca(group), ctx.rest(group)
    size = ctx.options.sizes[-1]
    work = Path(tempfile.mkdtemp(prefix="atli-followup-"))
    results = {"size": size}
    renderer = None
    try:
        environment = credentials(work)
        spike.write_requests(work, work / "credentials")
        if "wake_client" not in ctx.state:
            client, _ = aca.create("--disk", "python-3.12", "--cpu", "1000m", "--memory", "2048Mi",
                                   "--egress-default", "Deny", "--egress-rule", "*.adcproxy.io:Allow",
                                   "--egress-rule", f"management.{ctx.region}.azuredevcompute.io:Allow",
                                   "--label", "role=wake-client")
            ctx.state.save(wake_client=client)
        client = ctx.state["wake_client"]
        aca.write(client, "/root/spike/wake.py", HERE / "wake.py")
        aca.write(client, "/root/spike/api-key", work / "api-key")
        for fixture in ["invoice", "long-table"]:
            aca.write(client, f"/root/spike/{fixture}.json", work / f"{fixture}.json")
        # The engine runs the size's planned conversions at once and queues the rest; the per-caller
        # limit is raised so that a burst measures the platform, not the server's 429s.
        renderer, _ = rest.renderer(ctx.state["disk"], environment(SIZES[size][2], 16), size, "wake")
        on_demand_port = {"port": 8080, "auth": {"anonymous": True}, "activationMode": "OnDemand"}
        url = rest.set_port(renderer, on_demand_port)["url"]
        results["ready_after_create_s"] = round(wait_ready(url), 2)

        def burst(bodies):
            output = aca.exec(client, f"cd /root/spike && python3 wake.py burst --url {url} --key-file api-key "
                                      + " ".join(f"--body {body}" for body in bodies), check=False, timeout=600)
            return next((json.loads(l) for l in output.stdout.splitlines() if l.startswith("{")),
                        {"error": output.stderr[-300:]})

        def stop():
            started = time.monotonic()
            rest.request("POST", f"sandboxes/{renderer}/stop", {})
            rest.wait(renderer, "Stopped")
            stopped_s = time.monotonic() - started
            time.sleep(5)
            return {"stopped_after_s": round(stopped_s, 2),
                    "state_details": rest.request("GET", f"sandboxes/{renderer}")[1].get("stateDetails")}

        results["warm"] = [burst(["invoice.json"]), burst(["long-table.json"])]
        for name, bodies, cycles in [("invoice", ["invoice.json"], 3),
                                     ("long_table", ["long-table.json"], 2),
                                     ("burst_4_invoices_2_long_tables",
                                      ["invoice.json"] * 4 + ["long-table.json"] * 2, 2)]:
            runs = []
            for _ in range(cycles):
                entry = stop()
                entry["wake"] = burst(bodies)
                entry["next_invoice"] = burst(["invoice.json"])
                entry["state_after"] = rest.state_of(renderer)
                log(f"  OnDemand {name}: {entry['wake'].get('first_pdf_s')} s to the first PDF, "
                    f"statuses {[r['status'] for r in entry['wake'].get('requests', [])]}")
                runs.append(entry)
            results[f"on_demand_{name}"] = runs

        # Manual activation: the proxy answers 403 and the caller resumes the renderer itself. The
        # data-plane token goes to the client in a file, is never printed, and is deleted after.
        rest.set_port(renderer, dict(on_demand_port, activationMode="Manual"))
        (work / "dp-token").write_text(rest.token)
        (work / "dp-token").chmod(0o600)
        aca.write(client, "/root/spike/dp-token", work / "dp-token")
        resume_url = f"{rest.base}/sandboxes/{renderer}/resume?api-version={API_VERSION}"
        manual = []
        try:
            for _ in range(3):
                entry = stop()
                output = aca.exec(client, f"cd /root/spike && python3 wake.py manual --url {url} --key-file api-key "
                                          f"--body invoice.json --resume-url '{resume_url}' --token-file dp-token",
                                  check=False, timeout=600)
                entry["manual"] = next((json.loads(l) for l in output.stdout.splitlines() if l.startswith("{")),
                                       {"error": output.stderr[-300:]})
                log(f"  Manual + resume: {entry['manual'].get('first_pdf_s')} s to the first PDF")
                manual.append(entry)
        finally:
            aca.exec(client, "rm -f /root/spike/dp-token", check=False)
        results["manual_resume"] = manual

        # Auto-suspend after OnDemand wakes: idle for 60 s, then the next request wakes it again.
        rest.set_port(renderer, on_demand_port)
        if rest.state_of(renderer) != "Running":
            rest.request("POST", f"sandboxes/{renderer}/resume", {})
            rest.wait(renderer, "Running")
        aca("sandbox", "lifecycle", "set", "--id", renderer, "--auto-suspend", "enable",
            "--idle-timeout-seconds", "60", "--mode", "Memory")
        auto = []
        for _ in range(2):
            before = burst(["invoice.json"])
            last = time.monotonic()
            while rest.state_of(renderer) != "Stopped" and time.monotonic() - last < 400:
                time.sleep(2)
            observed = round(time.monotonic() - last, 1)
            details = rest.request("GET", f"sandboxes/{renderer}")[1].get("stateDetails")
            time.sleep(5)
            wake = burst(["invoice.json"])
            auto.append({"request_before": before, "stopped_observed_after_s": observed,
                         "state_details": details, "wake": wake})
            log(f"  Auto-suspend: Stopped {observed} s after the last request ({details}); "
                f"wake {wake.get('first_pdf_s')} s")
        results["auto_suspend"] = auto
    finally:
        if renderer:
            rest.request("DELETE", f"sandboxes/{renderer}")
        shutil.rmtree(work, ignore_errors=True)
        ctx.record("ondemand", results)


# ---------------------------------------------------------------------------------------------
# cleanup
# ---------------------------------------------------------------------------------------------

def cleanup(ctx):
    role_cleanup(ctx)
    for key in ["renderer_group", "tester_group"]:
        if ctx.state.get(key):
            log(f"Deleting sandbox group {ctx.state[key]}")
            ctx.aca(ctx.state[key])("sandboxgroup", "delete", "--name", ctx.state[key], "--yes", check=False,
                                    timeout=3600)
    log(f"Deleting {ctx.resource_group}")
    az("group", "delete", "--name", ctx.resource_group, "--yes", "--no-wait", check=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("command", choices=["setup", "ingress", "role", "dns", "load", "ondemand", "cleanup"])
    parser.add_argument("--state", type=Path, default=Path(tempfile.gettempdir()) / "atli-sandboxes-followup/state.json")
    parser.add_argument("--output", type=Path, default=Path(tempfile.gettempdir()) / "atli-sandboxes-followup/results.json")
    parser.add_argument("--location", default="eastus2")
    parser.add_argument("--laptop-ip", help="ingress: this machine's public IPv4 address, for the allow-list test")
    parser.add_argument("--sizes", nargs="+", default=["L", "M"], choices=list(SIZES))
    parser.add_argument("--concurrency", nargs="+", type=int, default=[1, 2, 4, 8])
    parser.add_argument("--invoice-seconds", type=float, default=20)
    parser.add_argument("--long-seconds", type=float, default=45)
    parser.add_argument("--redo", action="store_true", help="load: rerun configurations already recorded")
    parser.add_argument("--skip-on-demand", action="store_true", help="load: skip the OnDemand activation test")
    parser.add_argument("--recommended", action="store_true",
                        help="load: also run one conversion per vCPU with a per-caller limit of twice that")
    options = parser.parse_args()
    for tool in ["az", "aca", "git", "openssl"]:
        if not shutil.which(tool):
            sys.exit(f"Missing required tool: {tool}")
    ctx = Context(options)
    if options.command == "ingress":
        if not options.laptop_ip:
            sys.exit("ingress needs --laptop-ip")
        ingress(ctx, options.laptop_ip)
    else:
        globals()[options.command](ctx)


if __name__ == "__main__":
    main()
