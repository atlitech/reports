#!/usr/bin/env python3
"""Conversions through the gateway, or straight to a renderer's port, from a client sandbox in the
region, as a customer's application would send them. production.py uploads and runs it.

  once     One request on a new connection: its status, seconds, size, whether it is a PDF, and
           the problem's kind (or the port proxy's error code) when it is not.
  latency  Sequential requests, one target after another, each target on a connection of its own
           that is kept alive: one first request per target (it may open the connection or wake a
           renderer), then --samples timed rounds. Prints every sample.
  load     Closed-loop load for several tenants at once, from a plan file: one process per tenant,
           as many workers as the tenant's concurrency, each on its own kept-alive connection,
           sending the invoice and every n-th time the 49-page report until the duration has
           passed. A 429 or a 503 with Retry-After makes that worker wait as long as it asks (at
           most 5 s), as a well-behaved caller would. Writes a summary: per tenant and fixture,
           PDFs per second and latency percentiles; every status by kind; a timeline in 10-second
           buckets; and the non-200 answers with their times.

Every request carries the API key read from --key-file (or the plan's key file), never printed.
Prints one JSON object. Python standard library only.

  python3 gateway-client.py once --url https://<host> --key-file api-key [--tenant t] --body invoice.json
  python3 gateway-client.py latency --plan latency.json
  python3 gateway-client.py load --plan load.json --out /root/run/load-1
"""
import argparse
import http.client
import json
import math
import multiprocessing
import os
import resource
import statistics
import sys
import threading
import time
import urllib.parse


def percentile(values, fraction):
    """Nearest-rank percentile."""
    if not values:
        return None
    ordered = sorted(values)
    return round(ordered[max(0, math.ceil(fraction * len(ordered)) - 1)], 4)


def summary(values):
    if not values:
        return {"n": 0}
    return {"n": len(values), "min": round(min(values), 4), "p50": percentile(values, 0.5),
            "p95": percentile(values, 0.95), "p99": percentile(values, 0.99), "max": round(max(values), 4),
            "mean": round(statistics.mean(values), 4)}


class Target:
    """Where requests go: a base URL, the headers (API key, tenant), and request bodies."""

    def __init__(self, url, key_file, tenant=None):
        parsed = urllib.parse.urlparse(url)
        self.host = parsed.netloc
        self.base = parsed.path.rstrip("/")
        self.headers = {"Content-Type": "application/json",
                        "X-Reports-Api-Key": open(key_file).read().strip()}
        if tenant:
            self.headers["X-Reports-Tenant"] = tenant

    def connect(self, timeout=180):
        return http.client.HTTPSConnection(self.host, timeout=timeout)


def send(connection, target, body, method="POST", path="/convert"):
    """One request on `connection`. Returns (result, connection_still_usable)."""
    started = time.monotonic()
    try:
        connection.request(method, target.base + path, body=body if method == "POST" else None,
                           headers=target.headers)
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
    if not result["pdf"]:
        try:
            problem = json.loads(data)
            # The server's problem details carry `kind`; the port proxy answers {"error", "errorCode"}.
            result["kind"] = problem.get("kind") or problem.get("errorCode") or problem.get("error")
            if problem.get("detail") or problem.get("title"):
                result["detail"] = (problem.get("detail") or problem.get("title"))[:160]
        except (ValueError, AttributeError):
            result["body"] = data[:120].decode(errors="replace")
    if retry_after:
        result["retry_after"] = retry_after
    if closing:
        connection.close()
    return result, not closing


def once(options):
    target = Target(options.url, options.key_file, options.tenant)
    body = open(options.body, "rb").read() if options.body else None
    connection = target.connect()
    result, _ = send(connection, target, body, options.method, options.path)
    connection.close()
    result["at"] = round(time.time(), 3)
    print(json.dumps(result))


def latency(options):
    """Plan: {"samples": n, "targets": [{"name", "url", "key_file", "tenant"?, "body"}]}."""
    plan = json.load(open(options.plan))
    targets = []
    for spec in plan["targets"]:
        target = Target(spec["url"], spec["key_file"], spec.get("tenant"))
        targets.append({"spec": spec, "target": target, "body": open(spec["body"], "rb").read(),
                        "connection": target.connect(), "first": None, "samples": [], "other": []})
    for entry in targets:
        entry["first"], usable = send(entry["connection"], entry["target"], entry["body"])
        if not usable:
            entry["connection"] = entry["target"].connect()
    for _ in range(plan["samples"]):
        for entry in targets:
            result, usable = send(entry["connection"], entry["target"], entry["body"])
            if result["status"] == 200 and result["pdf"]:
                entry["samples"].append(result["s"])
            else:
                entry["other"].append(result)
            if not usable:
                entry["connection"] = entry["target"].connect()
    output = {}
    for entry in targets:
        entry["connection"].close()
        output[entry["spec"]["name"]] = {"first": entry["first"], "samples_s": entry["samples"],
                                         "summary": summary(entry["samples"]), "not_pdf": entry["other"][:20],
                                         "not_pdf_count": len(entry["other"])}
    text = json.dumps(output)
    if options.out:
        with open(options.out, "w") as file:
            file.write(text + "\n")
    print(text)


def tenant_process(plan, spec, start_at, out_dir):
    """One tenant's workers; writes their requests to <out_dir>/<tenant>.json."""
    target = Target(plan["url"], plan["key_file"], spec["tenant"])
    bodies = {name: open(path, "rb").read() for name, path in plan["bodies"].items()}
    every = spec.get("report_every") or 0
    deadline = start_at + plan["duration"]
    records = []  # (offset s, seconds, status, kind, fixture, worker)
    lock = threading.Lock()

    def worker(index):
        connection = target.connect()
        count = index * 7  # spreads the reports of a tenant's workers apart
        while time.time() < start_at:
            time.sleep(0.01)
        while time.time() < deadline:
            count += 1
            fixture = "long-table" if every and count % every == 0 else "invoice"
            sent = time.time()
            result, usable = send(connection, target, bodies[fixture])
            if not usable:
                connection = target.connect()
            status = result["status"]
            if status == 200 and not result.get("pdf"):
                kind = "not-pdf"
            else:
                kind = result.get("kind") or result.get("error") or ""
            with lock:
                records.append((round(sent - start_at, 3), result["s"], status, kind, fixture, index))
            if status in (429, 503) and result.get("retry_after"):
                try:
                    time.sleep(min(float(str(result["retry_after"]).split(",")[0]), 5.0))
                except ValueError:
                    time.sleep(1.0)
            elif status != 200:
                time.sleep(0.25)
        connection.close()

    threads = [threading.Thread(target=worker, args=(i,), daemon=True) for i in range(spec["concurrency"])]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    usage = resource.getrusage(resource.RUSAGE_SELF)
    with open(os.path.join(out_dir, f"{spec['tenant']}.json"), "w") as file:
        json.dump({"records": records, "cpu_s": round(usage.ru_utime + usage.ru_stime, 2)}, file)


def load(options):
    """Plan: {"url", "key_file", "duration", "bodies": {"invoice": path, "long-table": path},
    "tenants": [{"tenant", "concurrency", "report_every"}]}."""
    plan = json.load(open(options.plan))
    os.makedirs(options.out, exist_ok=True)
    start_at = time.time() + 3
    with open(os.path.join(options.out, "started"), "w") as file:
        file.write(f"{start_at}\n")
    processes = [multiprocessing.Process(target=tenant_process, args=(plan, spec, start_at, options.out))
                 for spec in plan["tenants"]]
    for process in processes:
        process.start()
    for process in processes:
        process.join()
    duration = plan["duration"]
    tenants, timeline, failures, cpu = {}, {}, [], 0.0
    for spec in plan["tenants"]:
        data = json.load(open(os.path.join(options.out, f"{spec['tenant']}.json")))
        cpu += data["cpu_s"]
        records = data["records"]
        per_fixture = {}
        statuses = {}
        for offset, seconds, status, kind, fixture, worker in records:
            label = f"{status} {kind}".strip()
            statuses[label] = statuses.get(label, 0) + 1
            entry = per_fixture.setdefault(fixture, {"ok": [], "statuses": {}})
            entry["statuses"][label] = entry["statuses"].get(label, 0) + 1
            if status == 200 and kind != "not-pdf":
                entry["ok"].append(seconds)
            bucket = str(int(offset // 10) * 10)
            line = timeline.setdefault(bucket, {"ok": 0, "other": 0})
            line["ok" if status == 200 and kind != "not-pdf" else "other"] += 1
            if status != 200 or kind == "not-pdf":
                failures.append({"t": offset, "tenant": spec["tenant"], "status": status, "kind": kind,
                                 "s": seconds, "fixture": fixture})
        elapsed = max((offset + seconds for offset, seconds, *_ in records), default=0.0)
        tenants[spec["tenant"]] = {
            "concurrency": spec["concurrency"], "report_every": spec.get("report_every"),
            "requests": len(records), "elapsed_s": round(elapsed, 2), "statuses": statuses,
            "fixtures": {fixture: {"pdfs": len(entry["ok"]),
                                   "pdfs_per_s": round(len(entry["ok"]) / duration, 3),
                                   "latency_s": summary(entry["ok"]), "statuses": entry["statuses"]}
                         for fixture, entry in per_fixture.items()},
        }
    failures.sort(key=lambda failure: failure["t"])
    result = {"duration_s": duration, "start_at_epoch": round(start_at, 3), "tenants": tenants,
              "timeline_10s": dict(sorted(timeline.items(), key=lambda item: int(item[0]))),
              "non_200": failures[:400], "non_200_count": len(failures),
              "client": {"cpu_s": round(cpu, 1), "nproc": os.cpu_count(), "loadavg": os.getloadavg()}}
    with open(os.path.join(options.out, "summary.json"), "w") as file:
        json.dump(result, file)
    print(json.dumps({"done": True, "requests": sum(t["requests"] for t in tenants.values())}))


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
    lat = sub.add_parser("latency")
    lat.add_argument("--plan", required=True)
    lat.add_argument("--out")
    run = sub.add_parser("load")
    run.add_argument("--plan", required=True)
    run.add_argument("--out", required=True)
    options = parser.parse_args()
    {"once": once, "latency": latency, "load": load}[options.mode](options)


if __name__ == "__main__":
    sys.exit(main())
