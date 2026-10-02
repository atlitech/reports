#!/usr/bin/env python3
"""Disposable gVisor test environment. Privileged DinD is trusted test infrastructure only.

No host mounts, no published Docker endpoint, no modification of the default daemon. Workers
receive stdin/stdout and their image; the gateway's CLI wrapper controls only this disposable
daemon. The benchmark is sequential and exploratory, not a capacity or hostile-tenant proof.
"""
import argparse
import base64
import hashlib
from http.client import HTTPConnection
import json
import math
import os
from pathlib import Path
import re
import select
import shlex
import shutil
import socket
import statistics
import struct
import subprocess
import tarfile
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid

REPO = Path(__file__).resolve().parents[2]
DIND = "docker:29.8.2-dind@sha256:7dcdfc4a20246236f558175182ccace1eb15a41bd3eb119dd2284f393498b7c1"
GVISOR = "release-20260928.0"
BUNDLE_HASHES = {
    "aarch64": "b7e11d27cbd69370ed7addb6c0d1c33e70e57a153cee57b5fdc5344bf303c7eb",
    "x86_64": "f3ed9131bc252259df150e270154180188f9df56b73a2312940325e1f522a2d6",
}
BUSYBOX = "busybox@sha256:bdf57e528e45e4433820e045b29b4597825a1c9e38353532d90a01445013f82e"
LIMITS = ["--network=none", "--read-only", "--tmpfs=/tmp:rw,nosuid,nodev,size=512m,mode=1777",
          "--user=1654:1654", "--cap-drop=ALL", "--security-opt=no-new-privileges:true",
          "--memory=1073741824", "--memory-swap=1073741824", "--cpus=1", "--pids-limit=256"]


def run(command, **kwargs):
    return subprocess.run(command, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          timeout=kwargs.pop("timeout", 120), **kwargs)


def remove_owned(prefix, name, volumes=False):
    command = prefix + ["rm", "--force"] + (["--volumes"] if volumes else []) + [name]
    removed = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60)
    if removed.returncode:
        # --rm may have already removed a completed worker. Distinguish that case from
        # a daemon/permission failure; never silently claim successful cleanup.
        remaining = run(prefix + ["ps", "--all", "--filter", "name=^/" + name + "$", "--format", "{{.ID}}"], timeout=10)
        assert not remaining.stdout.strip(), "Owned test container could not be removed"


def pdf_info(pdf):
    assert pdf.startswith(b"%PDF") and pdf.rstrip().endswith(b"%%EOF"), "Invalid or truncated PDF"
    return {"bytes": len(pdf), "pages": len(re.findall(rb"/Type\s*/Page(?![A-Za-z])", pdf))}


def options(fixture, protocol):
    result = {"paperSize": {"width": 8.27, "height": 11.69} if protocol else "a4",
              "margins": {side: 0.4 for side in ("top", "bottom", "left", "right")},
              "printBackground": True, "generateTaggedPdf": True, "preferCssPageSize": False}
    if fixture == "chart":
        result.update({"waitForSignal": "reportReady"})
        result["waitTimeout" if protocol else "waitTimeoutSeconds"] = "00:00:30" if protocol else 30
    return result


class Lab:
    def __init__(self, args, work):
        self.args, self.work = args, work
        self.name = "atli-gvisor-test-" + uuid.uuid4().hex[:12]
        self.prefix = ["docker", "exec", "--interactive", self.name, "docker"]
        self.created = False
        self.baseline = None

    def inner(self, *args, **kwargs):
        return run(self.prefix + list(args), **kwargs)

    def prepare(self):
        arch = run(["docker", "info", "--format", "{{.Architecture}}"]).stdout.decode().strip()
        arch = {"arm64": "aarch64", "amd64": "x86_64"}.get(arch, arch)
        expected = BUNDLE_HASHES[arch]
        archive = self.work / ("gvisor-" + arch + ".tar.bz2")
        if self.args.bundle:
            shutil.copyfile(self.args.bundle, archive)
        else:
            with urllib.request.urlopen(f"https://github.com/google/gvisor/releases/download/{GVISOR}/{archive.name}", timeout=120) as response, archive.open("wb") as output:
                shutil.copyfileobj(response, output)
        with archive.open("rb") as source:
            assert hashlib.file_digest(source, "sha256").hexdigest() == expected, "gVisor checksum mismatch"
        bundle = self.work / "runtime"
        bundle.mkdir()
        with tarfile.open(archive) as package:
            package.extractall(bundle, filter="data")
        self.created = True  # The unique name is ours even if CLI startup times out after creation.
        run(["docker", "run", "--detach", "--privileged", "--name", self.name,
             "--env", "DOCKER_TLS_CERTDIR=", "--env", "ATLI_GATEWAY_CANARY=trusted-control-only",
             "--entrypoint", "/usr/local/bin/dind", DIND,
             "sleep", "infinity"], timeout=300)
        for item in ("runsc", "gvisor-bin"):
            run(["docker", "cp", str(bundle / item), self.name + ":/usr/local/bin/" + item])
        daemon = self.work / "daemon.json"
        daemon.write_text(json.dumps({"hosts": ["unix:///var/run/docker.sock"], "iptables": False,
            "ip6tables": False, "bridge": "none", "storage-driver": "vfs",
            "features": {"containerd-snapshotter": False},
            "runtimes": {"runsc": {"path": "/usr/local/bin/runsc",
                                    "runtimeArgs": ["--platform=systrap", "--network=none"]}}}))
        run(["docker", "exec", self.name, "mkdir", "-p", "/etc/docker"])
        run(["docker", "cp", str(daemon), self.name + ":/etc/docker/daemon.json"])
        run(["docker", "exec", "--detach", self.name, "sh", "-c", "dockerd > /tmp/dockerd.log 2>&1"])
        for attempt in range(60):
            try:
                runtimes = json.loads(self.inner("info", "--format", "{{json .Runtimes}}", timeout=3).stdout)
                assert "runsc" in runtimes, "Required runtime was not registered"
                break
            except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
                time.sleep(1)
        else:
            raise RuntimeError("Disposable Docker daemon did not start")
        # Transfer image contents through a pipe: no host socket or filesystem bind mount.
        saved = subprocess.Popen(["docker", "save", self.args.worker_image], stdout=subprocess.PIPE)
        try:
            self.inner("load", stdin=saved.stdout, timeout=180)
        finally:
            saved.stdout.close()
            try:
                saved.wait(10)
            except subprocess.TimeoutExpired:
                saved.kill()
                saved.wait(5)
        assert saved.returncode == 0
        self.inner("pull", BUSYBOX, timeout=180)
        return {"gvisor": GVISOR, "bundle_sha256": expected, "architecture": arch,
                "dind_image": DIND, "worker_image_id": self.inner("image", "inspect", "--format", "{{.Id}}", self.args.worker_image).stdout.decode().strip()}

    def canaries(self):
        # The outer daemon's private file and environment are deliberately never mounted/passed.
        run(["docker", "exec", self.name, "sh", "-c", "mkdir -p /gateway-only; printf canary > /gateway-only/secret"])
        # This listener is in the trusted daemon's network namespace. A positive control
        # proves it is reachable; the compromised sandbox must fail the identical connection.
        sentinel = "sentinel-" + uuid.uuid4().hex[:8]
        self.inner("run", "--detach", "--name", sentinel, "--network=host", BUSYBOX, "sh", "-c",
                   "mkdir -p /tmp/sentinel; printf reachable >/tmp/sentinel/index.html; exec httpd -f -p 18089 -h /tmp/sentinel")
        control = self.inner("run", "--rm", "--network=host", BUSYBOX, "wget", "-qO-", "http://127.0.0.1:18089").stdout
        assert control == b"reachable", "Network canary positive control failed"
        self.inner("run", "--rm", "--network=host", BUSYBOX, "sh", "-c", "command -v nc; nc -w 2 127.0.0.1 18089 </dev/null")
        name = "runtime-proof-" + uuid.uuid4().hex[:8]
        self.inner("run", "--detach", "--name", name, "--runtime=runsc", *LIMITS, BUSYBOX, "sleep", "60")
        try:
            actual = json.loads(self.inner("inspect", name).stdout)[0]
            assert actual["HostConfig"]["Runtime"] == "runsc"
            kernel = self.inner("exec", name, "uname", "-r").stdout.decode().strip()
            assert "gvisor" in kernel
            # Direct commands model a compromised worker process; these bypass browser interception.
            probe = """set -eu
              command -v nc >/dev/null
              test ! -e /var/run/docker.sock
              test ! -e /gateway-only/secret
              test ! -e /proc/1/root/gateway-only/secret
              test -z "${ATLI_GATEWAY_CANARY:-}"
              test "$(id -u)" = 1654
              ! touch /cannot-write-root 2>/dev/null
              ! mount -t tmpfs none /tmp 2>/dev/null
              ! nc -w 2 169.254.169.254 80 </dev/null
              ! nc -w 2 1.1.1.1 443 </dev/null
              ! nc -w 2 127.0.0.1 2375 </dev/null
              ! nc -w 2 127.0.0.1 18089 </dev/null
              printf own-data >/tmp/one-job-canary
            """
            self.inner("exec", name, "sh", "-c", probe)
            # Fresh container cannot read the previous sandbox's writable temporary filesystem.
            self.inner("run", "--rm", "--runtime=runsc", *LIMITS, BUSYBOX, "test", "!", "-e", "/tmp/one-job-canary")
            return {"trusted_daemon_runtime": "runsc", "guest_kernel": kernel,
                    "direct_probes": ["no daemon socket", "no gateway file", "no inherited gateway environment",
                                      "read-only root", "mount denied", "metadata/public/daemon TCP denied",
                                      "controlled listener reachable by ordinary control but denied to sandbox", "new sandbox has fresh tmp"]}
        finally:
            self.inner("rm", "--force", name)
            self.inner("rm", "--force", sentinel)

    def close(self):
        try:
            if self.baseline:
                remove_owned(["docker"], self.baseline)
        finally:
            if self.created:
                remove_owned(["docker"], self.name, volumes=True)


class Worker:
    def __init__(self, lab, jobs=1, runtime="runsc"):
        self.lab = lab
        self.name = "atli-protocol-" + uuid.uuid4().hex
        self.err = tempfile.TemporaryFile()
        self.process = subprocess.Popen(lab.prefix + ["run", "--rm", "--interactive", "--name", self.name,
            "--runtime=" + runtime, *LIMITS, "--init", "--env", f"ATLI_WORKER_MAX_JOBS={jobs}", lab.args.worker_image],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.err, bufsize=0)
        self.peak_memory = None
        self.cpu_usec = None

    def stats(self):
        # Read Linux cgroup v2 from the trusted outer daemon, outside gVisor's virtual /proc.
        try:
            identifier = self.lab.inner("inspect", "--format", "{{.Id}}", self.name, timeout=3).stdout.decode().strip()
            command = f"cat /sys/fs/cgroup/docker/{identifier}/cpu.stat /sys/fs/cgroup/docker/{identifier}/memory.peak"
            lines = run(["docker", "exec", self.lab.name, "sh", "-c", command], timeout=3).stdout.decode().splitlines()
            self.cpu_usec = next(int(line.split()[1]) for line in lines if line.startswith("usage_usec "))
            self.peak_memory = max(self.peak_memory or 0, int(lines[-1]))
            return True
        except (subprocess.SubprocessError, ValueError, StopIteration):
            return False  # An exited disposable worker may already have lost its cgroup.

    def read(self, count, deadline):
        chunks = []
        while count:
            remaining = deadline - time.monotonic()
            if remaining <= 0 or not select.select([self.process.stdout], [], [], remaining)[0]:
                raise TimeoutError("Worker response deadline exceeded")
            chunk = os.read(self.process.stdout.fileno(), count)
            if not chunk:
                raise RuntimeError("Worker ended before response completion; untrusted stderr omitted")
            chunks.append(chunk)
            count -= len(chunk)
        return b"".join(chunks)

    def frame(self, maximum, deadline):
        size, = struct.unpack("<i", self.read(4, deadline))
        assert 0 <= size <= maximum, "Invalid worker frame length"
        return self.read(size, deadline)

    def convert(self, html, settings, close_input=False):
        job = str(uuid.uuid4())
        body = json.dumps({"version": 1, "jobId": job, "html": html, "options": settings}, separators=(",", ":")).encode()
        assert len(body) <= 16 * 1024 * 1024
        failure = []
        def write():
            try:
                data = memoryview(struct.pack("<i", len(body)) + body)
                while data:
                    written = self.process.stdin.write(data)
                    if not written:
                        raise BrokenPipeError("Worker input pipe closed")
                    data = data[written:]
                self.process.stdin.flush()
                if close_input:
                    self.process.stdin.close()
            except Exception as error:
                failure.append(error)
        writer = threading.Thread(target=write, daemon=True)
        writer.start()
        deadline = time.monotonic() + 100
        header = json.loads(self.frame(16 * 1024, deadline))
        assert header["version"] == 1 and header["jobId"] == job
        if header["status"] != "pdf":
            raise RuntimeError("Worker rejected the benchmark document; untrusted details omitted")
        chunks, total = [], 0
        while chunk := self.frame(64 * 1024, deadline):
            chunks.append(chunk)
            total += len(chunk)
            assert total <= 50 * 1024 * 1024
        writer.join(1)
        assert not writer.is_alive() and not failure
        return b"".join(chunks)

    def close(self):
        try:
            if not self.process.stdin.closed:
                self.process.stdin.close()
            self.process.wait(15)
            assert select.select([self.process.stdout], [], [], 2)[0], "Worker output did not reach EOF"
            assert self.process.stdout.read(1) == b"", "Worker sent trailing protocol output"
            assert self.process.returncode == 0, "Worker did not exit cleanly"
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(5)
            raise
        finally:
            remove_owned(self.lab.prefix, self.name)
            self.err.close()


def http(url, key, html=None, settings=None, timeout=100):
    data = json.dumps({"html": html, "options": settings or {}}).encode() if html is not None else None
    headers = {"Content-Type": "application/json"}
    if key:
        headers["X-Reports-Api-Key"] = key
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers), timeout=timeout) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


def auth_environment():
    credential = "benchmark." + os.urandom(32).hex()
    return credential, {
        "ReportsServer__Authentication__Mode": "ApiKey",
        "ReportsServer__Authentication__ApiKeys__0__Id": "benchmark",
        "ReportsServer__Authentication__ApiKeys__0__Hash": base64.b64encode(hashlib.sha256(credential.encode()).digest()).decode(),
        "ReportsServer__Authentication__ApiKeys__0__CallerId": "benchmark",
        "ReportsServer__Authentication__ApiKeys__0__Permissions__0": "reports.convert",
    }


class Gateway:
    def __init__(self, lab, runtime="runsc", timeout="00:01:30"):
        self.lab = lab
        self.key, authentication = auth_environment()
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            self.port = listener.getsockname()[1]
        self.url = f"http://127.0.0.1:{self.port}"
        # Resolve the operator's endpoint once. The worker never receives this wrapper,
        # host socket, Docker credentials, or gateway environment.
        endpoint = run(["docker", "context", "inspect", "--format", "{{.Endpoints.docker.Host}}"]).stdout.decode().strip()
        endpoint = os.environ.get("DOCKER_HOST", endpoint)
        wrapper = lab.work / "owned-docker-wrapper"
        wrapper.write_text("#!/bin/sh\nexec " + " ".join(shlex.quote(item) for item in [
            shutil.which("docker"), "--host", endpoint, "exec", "--interactive", lab.name, "docker"]) + ' "$@"\n')
        wrapper.chmod(0o700)
        environment = {name: value for name, value in os.environ.items()
                       if name in ("PATH", "HOME", "DOTNET_ROOT", "TMPDIR", "LANG")}
        environment.update(authentication)
        environment.update({
            "ASPNETCORE_URLS": self.url,
            "ATLI_GATEWAY_CANARY": "must-not-reach-worker",
            "ReportsServer__Execution__Mode": "Worker",
            "ReportsServer__Execution__Backend": "Docker",
            "ReportsServer__Execution__DockerExecutablePath": str(wrapper),
            "ReportsServer__Execution__Image": lab.args.worker_image,
            "ReportsServer__Execution__Runtime": runtime,
            "ReportsServer__Execution__MaxConcurrentJobs": "1",
            "ReportsServer__Execution__Timeout": timeout,
            "ReportsServer__Execution__CleanupTimeout": "00:00:15",
        })
        self.logs = tempfile.TemporaryFile()
        self.process = subprocess.Popen([lab.args.dotnet, str(lab.args.gateway_dll.resolve())],
            env=environment, cwd=lab.work, stdin=subprocess.DEVNULL, stdout=self.logs, stderr=self.logs)
        try:
            for attempt in range(60):
                try:
                    if http(self.url + "/health/live", None, timeout=2)[0] == 200:
                        break
                except (OSError, TimeoutError):
                    pass
                if self.process.poll() is not None:
                    raise RuntimeError("Gateway exited during startup; untrusted logs omitted")
                time.sleep(.5)
            else:
                raise RuntimeError("Gateway did not become live")
        except BaseException:
            self.close()
            raise

    def workers(self):
        return self.lab.inner("ps", "--all", "--filter", "name=atli-reports-worker-", "--format", "{{.Names}}").stdout.decode().split()

    def wait_worker(self):
        for attempt in range(100):
            names = self.workers()
            if names:
                return names[0]
            time.sleep(.1)
        raise AssertionError("Expected an active worker")

    def wait_clean(self):
        for attempt in range(100):
            if not self.workers():
                return
            time.sleep(.2)
        raise AssertionError("Gateway left a worker container behind")

    def close(self):
        if self.logs.closed:
            return
        self.process.terminate()
        try:
            self.process.wait(20)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(5)
        self.logs.close()


def gateway_smoke(lab):
    gateway = Gateway(lab)
    checks = []
    try:
        assert http(gateway.url + "/health/ready", None)[0] == 200
        assert http(gateway.url + "/convert", None, "<h1>Unauthorized</h1>")[0] == 401
        assert not gateway.workers(), "Unauthorized request launched a worker"
        checks.append("authentication precedes worker launch")
        status, pdf = http(gateway.url + "/convert", gateway.key, "<h1>Isolated HTTP conversion</h1>")
        assert status == 200, f"Gateway conversion returned {status}"
        pdf_info(pdf)
        gateway.wait_clean()
        checks.append("authenticated PDF and disposable cleanup")
        status, _ = http(gateway.url + "/convert", gateway.key, '<img src="http://127.0.0.1:18089/">')
        assert status == 422, f"Policy denial returned {status}"
        gateway.wait_clean()
        checks.append("render policy denial and cleanup")

        # Hold a real job open, inspect operator-enforced settings, then disconnect the caller.
        connection = HTTPConnection("127.0.0.1", gateway.port, timeout=100)
        data = json.dumps({"html": "<h1>Waiting</h1>", "options": {"waitForSignal": "never", "waitTimeoutSeconds": 60}})
        connection.request("POST", "/convert", data, {"Content-Type": "application/json", "X-Reports-Api-Key": gateway.key})
        active = gateway.wait_worker()
        actual = json.loads(lab.inner("inspect", active).stdout)[0]
        host = actual["HostConfig"]
        assert host["Runtime"] == "runsc" and host["NetworkMode"] == "none" and host["ReadonlyRootfs"]
        assert host["Memory"] == 1073741824 and host["NanoCpus"] == 1000000000 and host["PidsLimit"] == 256
        assert actual["Config"]["User"] == "1654:1654" and not actual["Mounts"]
        assert all("ATLI_GATEWAY_CANARY" not in value and "Authentication" not in value for value in actual["Config"]["Env"])
        # Close the socket immediately; do not wait for the server's pending response.
        connection.sock.shutdown(socket.SHUT_RDWR)
        connection.close()
        gateway.wait_clean()
        checks.append("caller disconnect cleanup and enforced launch settings")

        response, failure = [], []
        def pending():
            try:
                response.append(http(gateway.url + "/convert", gateway.key, "<h1>Crash test</h1>",
                                     {"waitForSignal": "never", "waitTimeoutSeconds": 60}))
            except Exception as error:
                failure.append(error)
        thread = threading.Thread(target=pending, daemon=True)
        thread.start()
        active = gateway.wait_worker()
        lab.inner("kill", active)
        thread.join(30)
        assert not thread.is_alive() and not failure and response[0][0] >= 500
        gateway.wait_clean()
        checks.append("worker crash cleanup")
    finally:
        gateway.close()
    gateway = Gateway(lab, timeout="00:00:02")
    try:
        status, _ = http(gateway.url + "/convert", gateway.key, "<h1>Deadline</h1>",
                         {"waitForSignal": "never", "waitTimeoutSeconds": 60})
        assert status == 504, f"Deadline returned {status}"
        gateway.wait_clean()
        checks.append("deadline cleanup")
    finally:
        gateway.close()
    gateway = Gateway(lab, runtime="atli-intentionally-missing-runtime")
    try:
        assert http(gateway.url + "/health/ready", None)[0] == 503
        status, _ = http(gateway.url + "/convert", gateway.key, "<h1>No fallback</h1>")
        assert status >= 500
        gateway.wait_clean()
        checks.append("missing runtime fails closed without fallback")
    finally:
        gateway.close()
    return checks


def worker_input_deadline(lab):
    worker = Worker(lab)
    started = time.monotonic()
    try:
        # Keep stdin open without sending a frame. Cancellation of OS pipe reads must not
        # defeat the worker's own input deadline, independently of gateway supervision.
        code = worker.process.wait(30)
        elapsed = time.monotonic() - started
        assert code == 2, f"Idle worker exited with {code}, expected protocol failure"
        assert worker.process.stdout.read(1) == b"", "Idle worker emitted protocol data"
        return {"exit_code": code, "elapsed_seconds": elapsed, "stdin_open": True}
    finally:
        remove_owned(lab.prefix, worker.name)
        if worker.process.poll() is None:
            worker.process.kill()
            worker.process.wait(5)
        worker.process.stdin.close()
        worker.err.close()


def baseline(lab):
    credential, environment = auth_environment()
    envfile = lab.work / "server.env"
    environment["ReportsEngine__Concurrency__MaxConcurrentConversions"] = "1"
    envfile.write_text("\n".join(f"{name}={value}" for name, value in environment.items()) + "\n")
    lab.baseline = "atli-integrated-bench-" + uuid.uuid4().hex[:12]
    # Same CPU/memory/read-only settings. HTTP requires its own loopback published port.
    baseline_limits = [item for item in LIMITS if item != "--network=none"]
    run(["docker", "run", "--detach", "--name", lab.baseline, *baseline_limits,
         "--publish", "127.0.0.1::8080", "--env-file", str(envfile), lab.args.server_image])
    address = run(["docker", "port", lab.baseline, "8080/tcp"]).stdout.decode().splitlines()[0]
    url = "http://" + address
    for attempt in range(90):
        try:
            if http(url + "/health/ready", None, timeout=2)[0] == 200:
                return url, credential
        except (OSError, TimeoutError):
            pass
        time.sleep(1)
    raise RuntimeError("Integrated baseline did not become ready")


def benchmark(lab):
    url, key = baseline(lab)
    fixtures = {name: (REPO / "benchmarks/fixtures" / (name + ".html")).read_text() for name in ("invoice", "assets", "long-table", "chart")}
    records = []
    for mode in ("integrated-warm", "gvisor-disposable", "gvisor-warm-sequential", "runc-warm-sequential"):
        # The ordinary runc control is benchmark-only and makes no containment claim.
        worker = Worker(lab, jobs=min(100, 4 * (lab.args.samples + 1) + 1),
                        runtime="runc" if mode == "runc-warm-sequential" else "runsc") if mode.endswith("warm-sequential") else None
        try:
            for fixture, html in fixtures.items():
                timings, pdf = [], b""
                for sample in range(lab.args.samples + 1):
                    active = worker
                    started = time.perf_counter()
                    if mode == "integrated-warm":
                        status, pdf = http(url + "/convert", key, html, options(fixture, False))
                        assert status == 200, f"Integrated conversion returned {status}"
                    else:
                        if active is None:
                            active = Worker(lab)
                        try:
                            pdf = active.convert(html, options(fixture, True), close_input=worker is None)
                        finally:
                            if worker is None:
                                active.close()
                    elapsed = time.perf_counter() - started
                    info = pdf_info(pdf)
                    if sample == 0:
                        cold = elapsed
                    else:
                        timings.append(elapsed)
                cpu_before = worker.cpu_usec if worker else None
                observed = worker.stats() if worker else False
                record = {"mode": mode, "fixture": fixture, "samples": len(timings), "errors": 0,
                          "first_job_seconds": cold, "p50_seconds": statistics.median(timings),
                          "first_job_includes_worker_startup": mode == "gvisor-disposable" or (worker is not None and fixture == "invoice"),
                          "p95_seconds": sorted(timings)[math.ceil(.95 * len(timings)) - 1],
                          "sequential_reports_per_second": len(timings) / sum(timings), **info,
                          "sandbox_cgroup_peak_bytes": worker.peak_memory if observed else None,
                          "sandbox_cpu_usec_total": worker.cpu_usec if observed else None,
                          "sandbox_cpu_usec_since_previous_fixture": worker.cpu_usec - cpu_before if observed and cpu_before is not None else None}
                records.append(record)
        finally:
            if worker:
                worker.close()
        for record in records:
            if record["mode"] == mode:
                print(json.dumps(record), flush=True)
    return records


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--worker-image", default="atli-reports-worker:security")
    parser.add_argument("--server-image", default="atli-reports-server:security")
    parser.add_argument("--bundle", type=Path, help="Optional cached official architecture tarball; checksum is still verified")
    parser.add_argument("--samples", type=int, default=5)
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    parser.add_argument("--gateway-dll", type=Path, default=REPO / "artifacts/bin/Atli.Reports.Server/debug/Atli.Reports.Server.dll")
    parser.add_argument("--skip-benchmark", action="store_true", help="Run only containment and gateway smoke checks")
    parser.add_argument("--output", type=Path, default=Path("artifacts/isolated-worker-results.json"))
    args = parser.parse_args()
    assert 1 <= args.samples <= 20
    result = {"limits": {"cpu": 1, "memory_bytes": 1073741824, "pids": 256, "tmp_bytes": 536870912},
              "scope": "Sequential exploratory microbenchmark, warm image cache, one trust domain. Not a capacity, cost, or cross-tenant safety claim.",
              "metrics": "Worker CPU and peak memory come from trusted host cgroup v2, including gVisor sandbox/gofer; peak values cumulative and CPU deltas span fixture boundaries including the first warmup. Disposable cgroups vanish before reliable final reads; metrics are null. HTTP baseline and nested Docker stdio transports differ. The runc warm control uses the same stdio transport and image but has no gVisor isolation. First job per fixture is a separate warmup; only the first fixture starts a warm worker. Every disposable job includes startup and cleanup. Small-sample p95 is exploratory, not a capacity or concurrency measurement."}
    result["source"] = {"revision": run(["git", "rev-parse", "HEAD"], cwd=REPO).stdout.decode().strip(),
                        "dirty": bool(run(["git", "status", "--porcelain"], cwd=REPO).stdout.strip())}
    docker_info = json.loads(run(["docker", "info", "--format", "{{json .}}"]).stdout)
    result["measurement_host"] = {key: docker_info[key] for key in
                                  ("ServerVersion", "KernelVersion", "OperatingSystem", "Architecture", "NCPU", "MemTotal")}
    result["integrated_image_id"] = run(["docker", "image", "inspect", "--format", "{{.Id}}", args.server_image]).stdout.decode().strip()
    result["chrome_version"] = run(["docker", "run", "--rm", "--entrypoint", "/opt/chrome-headless-shell/chrome-headless-shell", args.worker_image, "--version"]).stdout.decode().strip()
    with tempfile.TemporaryDirectory(prefix="atli-isolated-workers-") as directory:
        lab = Lab(args, Path(directory))
        try:
            result["runtime"] = lab.prepare()
            print("Verified runtime bundle and disposable daemon ready", flush=True)
            result["containment_canaries"] = lab.canaries()
            result["worker_idle_input_deadline"] = worker_input_deadline(lab)
            result["gateway_smoke"] = gateway_smoke(lab)
            print("Containment and HTTP gateway lifecycle checks passed", flush=True)
            if not args.skip_benchmark:
                result["benchmark"] = benchmark(lab)
        finally:
            lab.close()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print("Wrote " + str(args.output), flush=True)


if __name__ == "__main__":
    main()
