#!/usr/bin/env python3
"""Closed-loop load against one renderer's POST /convert, run from a client sandbox in the region.

<concurrency> workers each keep one HTTPS connection to the renderer's port URL (through the
platform's port proxy) and send the same request body back to back until <duration> seconds have
passed; a request started before the deadline is allowed to finish. Each worker sends one warmup
request first, then all workers start together. Prints one JSON object: statuses, PDFs per second,
and latency percentiles of the successful conversions. Python standard library only; the API key is
read from a file and never printed. followup.py uploads and runs it.

  python3 load.py --url https://<id>--8080.<region>.adcproxy.io --key-file api-key \
      --body invoice.json --concurrency 4 --duration 30 [--label invoice-c4]
"""
import argparse
import http.client
import json
import math
import statistics
import threading
import time
import urllib.parse


def percentile(values, fraction):
    """Nearest-rank percentile of a list of seconds."""
    if not values:
        return None
    ordered = sorted(values)
    return round(ordered[max(0, math.ceil(fraction * len(ordered)) - 1)], 4)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--url", required=True)
    parser.add_argument("--key-file", required=True)
    parser.add_argument("--body", required=True)
    parser.add_argument("--concurrency", type=int, required=True)
    parser.add_argument("--duration", type=float, required=True)
    parser.add_argument("--label", default="")
    parser.add_argument("--timeout", type=float, default=180)
    parser.add_argument("--honor-retry-after", action="store_true",
                        help="after a 429 or 503, wait for its Retry-After before the next request")
    options = parser.parse_args()

    host = urllib.parse.urlparse(options.url).netloc
    body = open(options.body, "rb").read()
    headers = {"Content-Type": "application/json", "X-Reports-Api-Key": open(options.key_file).read().strip()}
    samples = []  # (start offset s, latency s, status, bytes, is_pdf, worker)
    lock = threading.Lock()
    clock = {}
    # The barrier's action runs once, before any worker is released: the measurement starts there.
    ready = threading.Barrier(options.concurrency + 1, action=lambda: clock.__setitem__("start", time.monotonic()))

    errors = {}  # the first non-PDF response per status: its Retry-After and the start of its body

    def send(connection):
        started = time.monotonic()
        retry_after = None
        try:
            connection.request("POST", "/convert", body=body, headers=headers)
            response = connection.getresponse()
            data = response.read()
            status = response.status
            retry_after = response.getheader("Retry-After")
        except (http.client.HTTPException, OSError) as error:
            connection.close()
            data, status = str(error).encode(), 0
        if data[:5] != b"%PDF-":
            with lock:
                errors.setdefault(str(status), {"retry_after": retry_after,
                                                "body": data[:160].decode(errors="replace")})
        return started, time.monotonic() - started, status, len(data), data[:5] == b"%PDF-", retry_after

    def wait_seconds(retry_after):
        try:
            return min(float(str(retry_after).split(",")[0]), 5.0)
        except ValueError:
            return 1.0

    finished = [False] * options.concurrency

    def worker(index):
        connection = http.client.HTTPSConnection(host, timeout=options.timeout)
        send(connection)  # warmup: TLS handshake, and a first conversion
        ready.wait()
        while time.monotonic() - clock["start"] < options.duration:
            try:
                started, seconds, status, size, is_pdf, retry_after = send(connection)
                if status == 0:
                    connection = http.client.HTTPSConnection(host, timeout=options.timeout)
                with lock:
                    samples.append((started - clock["start"], seconds, status, size, is_pdf, index))
                if options.honor_retry_after and status in (429, 503):
                    # As a well-behaved gateway would: wait as long as the renderer asks (at most 5 s).
                    time.sleep(wait_seconds(retry_after or 1))
            except Exception as error:  # recorded, never fatal to the worker
                with lock:
                    errors.setdefault("exception", repr(error)[:200])
                time.sleep(0.1)
        finished[index] = True

    threads = [threading.Thread(target=worker, args=(i,), daemon=True) for i in range(options.concurrency)]
    for thread in threads:
        thread.start()
    ready.wait()
    for thread in threads:
        thread.join()
    elapsed = max((offset + seconds for offset, seconds, *_ in samples), default=0.0)

    ok = [seconds for _, seconds, status, _, is_pdf, _ in samples if status == 200 and is_pdf]
    statuses = {}
    for _, _, status, _, _, _ in samples:
        statuses[str(status)] = statuses.get(str(status), 0) + 1
    print(json.dumps({
        "label": options.label,
        "concurrency": options.concurrency,
        "duration_s": options.duration,
        "elapsed_s": round(elapsed, 3),
        "requests": len(samples),
        "pdfs": len(ok),
        "statuses": statuses,
        "pdfs_per_s": round(len(ok) / elapsed, 3) if elapsed else None,
        "p50_s": percentile(ok, 0.50),
        "p95_s": percentile(ok, 0.95),
        "min_s": round(min(ok), 4) if ok else None,
        "max_s": round(max(ok), 4) if ok else None,
        "mean_s": round(statistics.mean(ok), 4) if ok else None,
        "pdf_bytes": next((size for _, _, status, size, is_pdf, _ in samples if is_pdf), None),
        "workers_finished": sum(finished),
        "errors": errors,
    }))


if __name__ == "__main__":
    main()
