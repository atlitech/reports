#!/usr/bin/env python3
"""Requests of applications whose workspaces each have a tenant of their own, sent through the
gateway from a client sandbox in the region. workspaces.py uploads and runs it.

  once     One request on a new connection: a conversion (POST /convert) or a deletion
           (DELETE /tenants/<id>). Prints its status, seconds, size, whether it is a PDF, and for a
           problem its kind, detail, and Retry-After.
  probe    One HTTPS GET to an address with a chosen TLS server name and Host header, and the name's
           DNS answer as this machine sees it: whether an internal address can be reached.
  streams  Several streams of requests at once, from a plan file, all starting together:
             burst     each tenant's first conversion at once (`copies` requests per tenant), each
                       on its own connection; a 429 or 503 with Retry-After waits that long (at most
                       `max_wait` s) and sends again, until a PDF or the stream's deadline;
             periodic  each tenant converts every `interval` s on a fixed schedule (the 49-page
                       report every `report_every`-th time), starting at `start` s, spread over the
                       interval; with `retry`, a 429 or 503 with Retry-After is sent again after it;
             flood     `workers` threads in `processes` processes send conversions as fast as they
                       are answered, each for a tenant ID never used before, honouring nothing.
           Writes every request to <out>/records.json and prints {"done": true, ...} at the end.

Every request carries the API key read from the stream's key file, never printed. Python standard
library only.

  python3 workspace-client.py once --url https://<host> --key-file app-a.key --tenant appa-1 --body invoice.json
  python3 workspace-client.py once --url https://<host> --key-file app-a.key --method DELETE --path /tenants/appa-1
  python3 workspace-client.py probe --connect <host or address> --sni <name> --host <name> --path /health/live
  python3 workspace-client.py streams --plan plan.json --out out-1
"""
import argparse
import http.client
import json
import multiprocessing
import os
import socket
import ssl
import sys
import threading
import time
import urllib.parse

TENANT_HEADER = "X-Reports-Tenant"


class Target:
    def __init__(self, url, key_file):
        parsed = urllib.parse.urlparse(url)
        self.host = parsed.netloc
        self.key = open(key_file).read().strip()

    def connect(self, timeout=150):
        return http.client.HTTPSConnection(self.host, timeout=timeout)


def send(connection, target, tenant, body, method="POST", path="/convert"):
    """One request on `connection`: (result, connection still usable)."""
    headers = {"X-Reports-Api-Key": target.key}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if tenant:
        headers[TENANT_HEADER] = tenant
    started = time.monotonic()
    try:
        connection.request(method, path, body=body, headers=headers)
        response = connection.getresponse()
        data = response.read()
        status = response.status
        retry_after = response.getheader("Retry-After")
        closing = (response.getheader("Connection") or "").lower() == "close"
    except (http.client.HTTPException, OSError) as error:
        connection.close()
        return {"status": 0, "s": round(time.monotonic() - started, 4), "error": type(error).__name__}, False
    result = {"status": status, "s": round(time.monotonic() - started, 4), "bytes": len(data),
              "pdf": data[:5] == b"%PDF-"}
    if not result["pdf"] and data:
        try:
            problem = json.loads(data)
            result["kind"] = problem.get("kind") or problem.get("errorCode") or problem.get("error")
            if problem.get("detail") or problem.get("title"):
                result["detail"] = (problem.get("detail") or problem.get("title"))[:200]
        except (ValueError, AttributeError):
            result["body"] = data[:120].decode(errors="replace")
    if retry_after:
        result["retry_after"] = retry_after
    if closing:
        connection.close()
    return result, not closing


def wait_for(result, max_wait):
    """Seconds a well-behaved caller waits before sending again, or None."""
    if result["status"] in (429, 503) and result.get("retry_after"):
        try:
            return min(float(str(result["retry_after"]).split(",")[0]), max_wait)
        except ValueError:
            return 1.0
    return None


def once(options):
    target = Target(options.url, options.key_file)
    body = open(options.body, "rb").read() if options.body else None
    connection = target.connect()
    result, _ = send(connection, target, options.tenant, body, options.method, options.path)
    connection.close()
    result["at"] = round(time.time(), 3)
    print(json.dumps(result))


def probe(options):
    """GET https://<connect><path> with the given TLS server name and Host header."""
    result = {"connect": "<given>", "path": options.path}
    try:
        result["dns"] = sorted({info[4][0] for info in socket.getaddrinfo(options.sni, 443)})
        result["dns"] = [("private" if a.startswith(("10.", "192.168.", "172.")) else "public")
                         for a in result["dns"]]
    except OSError as error:
        result["dns"] = f"error: {type(error).__name__} {getattr(error, 'errno', '')}"
    started = time.monotonic()
    try:
        context = ssl.create_default_context()
        if options.insecure:
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
        raw = socket.create_connection((options.connect, 443), timeout=10)
        sock = context.wrap_socket(raw, server_hostname=options.sni)
        request = (f"GET {options.path} HTTP/1.1\r\nHost: {options.host}\r\nConnection: close\r\n"
                   f"User-Agent: workspace-client-probe\r\n\r\n")
        sock.sendall(request.encode())
        data = b""
        while len(data) < 4096:
            chunk = sock.recv(4096)
            if not chunk:
                break
            data += chunk
        sock.close()
        head = data.split(b"\r\n\r\n", 1)
        result["status_line"] = head[0].split(b"\r\n", 1)[0].decode(errors="replace")[:80]
        result["body"] = (head[1] if len(head) > 1 else b"")[:160].decode(errors="replace")
    except (OSError, ssl.SSLError) as error:
        result["error"] = f"{type(error).__name__}: {str(error)[:160]}"
    result["s"] = round(time.monotonic() - started, 3)
    print(json.dumps(result))


# ---------------------------------------------------------------------------------------------
# streams
# ---------------------------------------------------------------------------------------------

def record(sink, lock, stream, tenant, offset, result, fixture, **extra):
    entry = {"stream": stream, "tenant": tenant, "t": round(offset, 3), "s": result["s"],
             "status": result["status"], "pdf": result.get("pdf", False), "fixture": fixture}
    for key in ["kind", "detail", "retry_after", "error", "bytes"]:
        if result.get(key) is not None:
            entry[key] = result[key]
    entry.update(extra)
    with lock:
        sink.append(entry)


def run_burst(spec, bodies, start_at, sink, lock):
    target = Target(spec["url"], spec["key_file"])
    deadline = start_at + spec.get("duration", 600)
    max_wait = spec.get("max_wait", 65)

    def one(tenant, copy):
        connection = target.connect()
        attempt = 0
        while time.time() < start_at:
            time.sleep(0.001)
        first = time.time()
        while time.time() < deadline:
            attempt += 1
            sent = time.time()
            result, usable = send(connection, target, tenant, bodies["invoice"])
            if not usable:
                connection = target.connect()
            record(sink, lock, spec["name"], tenant, sent - start_at, result, "invoice", attempt=attempt,
                   copy=copy, since_first=round(time.time() - first, 3))
            if result["status"] == 200 and result.get("pdf"):
                break
            pause = wait_for(result, max_wait)
            time.sleep(pause if pause is not None else 1.0)
        connection.close()

    threads = [threading.Thread(target=one, args=(tenant, copy), daemon=True)
               for tenant in spec["tenants"] for copy in range(spec.get("copies", 1))]
    return threads


def run_periodic(spec, bodies, start_at, sink, lock):
    target = Target(spec["url"], spec["key_file"])
    interval = spec["interval"]
    every = spec.get("report_every") or 0
    stop_at = start_at + spec.get("start", 0) + spec["duration"]
    tenants = spec["tenants"]
    max_wait = spec.get("max_wait", 65)

    def one(index, tenant):
        connection = target.connect()
        begin = start_at + spec.get("start", 0) + (index * interval / max(1, len(tenants)) if spec.get("spread", True) else 0)
        count = index * 7 if every else 0  # spreads the tenants' reports apart
        seq = 0
        next_at = begin
        while next_at < stop_at:
            delay = next_at - time.time()
            if delay > 0:
                time.sleep(delay)
            count += 1
            seq += 1
            fixture = "long-table" if every and count % every == 0 else "invoice"
            attempt = 0
            while True:
                attempt += 1
                sent = time.time()
                result, usable = send(connection, target, tenant, bodies[fixture])
                if not usable:
                    connection = target.connect()
                record(sink, lock, spec["name"], tenant, sent - start_at, result, fixture, seq=seq, attempt=attempt)
                pause = wait_for(result, max_wait) if spec.get("retry") else None
                if pause is None or time.time() + pause >= stop_at:
                    break
                time.sleep(pause)
            next_at += interval
            # A request that outlasted the interval: the next one goes at once, not in a burst.
            next_at = max(next_at, time.time())
        connection.close()

    return [threading.Thread(target=one, args=(i, t), daemon=True) for i, t in enumerate(tenants)]


def run_delete(spec, bodies, start_at, sink, lock):
    """DELETE /tenants/<id> for each tenant, `workers` at a time."""
    target = Target(spec["url"], spec["key_file"])
    queue = list(spec["tenants"])
    queue_lock = threading.Lock()

    def worker():
        connection = target.connect()
        while time.time() < start_at:
            time.sleep(0.001)
        while True:
            with queue_lock:
                if not queue:
                    break
                tenant = queue.pop(0)
            sent = time.time()
            result, usable = send(connection, target, None, None, "DELETE", f"/tenants/{tenant}")
            if not usable:
                connection = target.connect()
            record(sink, lock, spec["name"], tenant, sent - start_at, result, None)
        connection.close()

    return [threading.Thread(target=worker, daemon=True) for _ in range(spec.get("workers", 1))]


def flood_process(spec, bodies, start_at, process_index, out_path):
    target = Target(spec["url"], spec["key_file"])
    sink, lock = [], threading.Lock()
    stop_at = start_at + spec.get("start", 0) + spec["duration"]

    def worker(index):
        connection = target.connect()
        n = 0
        while time.time() < start_at + spec.get("start", 0):
            time.sleep(0.001)
        while time.time() < stop_at:
            n += 1
            tenant = f"{spec['prefix']}{process_index}-{index}-{n}"
            sent = time.time()
            result, usable = send(connection, target, tenant, bodies["invoice"])
            if not usable:
                connection = target.connect()
            record(sink, lock, spec["name"], tenant, sent - start_at, result, "invoice")
        connection.close()

    threads = [threading.Thread(target=worker, args=(i,), daemon=True) for i in range(spec["workers"])]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    with open(out_path, "w") as file:
        json.dump(sink, file)


def streams(options):
    """Plan: {"url", "bodies": {"invoice": path, "long-table": path}, "streams": [...]}; each
    stream has "name", "type", "key_file", and its type's settings; "url" may be per stream."""
    plan = json.load(open(options.plan))
    os.makedirs(options.out, exist_ok=True)
    bodies = {name: open(path, "rb").read() for name, path in plan["bodies"].items()}
    start_at = time.time() + 3
    with open(os.path.join(options.out, "started"), "w") as file:
        file.write(f"{start_at}\n")
    sink, lock = [], threading.Lock()
    threads, processes = [], []
    for spec in plan["streams"]:
        spec = dict(spec)
        spec.setdefault("url", plan["url"])
        if spec["type"] == "burst":
            threads += run_burst(spec, bodies, start_at, sink, lock)
        elif spec["type"] == "periodic":
            threads += run_periodic(spec, bodies, start_at, sink, lock)
        elif spec["type"] == "delete":
            threads += run_delete(spec, bodies, start_at, sink, lock)
        elif spec["type"] == "flood":
            for index in range(spec.get("processes", 1)):
                path = os.path.join(options.out, f"flood-{spec['name']}-{index}.json")
                process = multiprocessing.Process(target=flood_process,
                                                  args=(spec, bodies, start_at, index, path))
                processes.append((process, path))
        else:
            raise SystemExit(f"unknown stream type {spec['type']}")
    for process, _ in processes:
        process.start()
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    for process, path in processes:
        process.join()
        if os.path.exists(path):
            sink.extend(json.load(open(path)))
    sink.sort(key=lambda entry: entry["t"])
    with open(os.path.join(options.out, "records.json"), "w") as file:
        json.dump({"start_at_epoch": round(start_at, 3), "records": sink,
                   "client": {"nproc": os.cpu_count(), "loadavg": os.getloadavg()}}, file)
    print(json.dumps({"done": True, "requests": len(sink)}))


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="mode", required=True)
    one = sub.add_parser("once")
    one.add_argument("--url", required=True)
    one.add_argument("--key-file", required=True)
    one.add_argument("--tenant")
    one.add_argument("--body")
    one.add_argument("--method", default="POST")
    one.add_argument("--path", default="/convert")
    pr = sub.add_parser("probe")
    pr.add_argument("--connect", required=True)
    pr.add_argument("--sni", required=True)
    pr.add_argument("--host", required=True)
    pr.add_argument("--path", default="/health/live")
    pr.add_argument("--insecure", action="store_true")
    st = sub.add_parser("streams")
    st.add_argument("--plan", required=True)
    st.add_argument("--out", required=True)
    options = parser.parse_args()
    {"once": once, "probe": probe, "streams": streams}[options.mode](options)


if __name__ == "__main__":
    sys.exit(main())
