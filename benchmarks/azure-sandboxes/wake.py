#!/usr/bin/env python3
"""Requests to a stopped renderer, from a client sandbox in the region, as a gateway would send them.

  burst   Sends every --body at once (one connection each) and prints, per request, its status,
          seconds, PDF size, and whether it is a PDF. With the port's activationMode OnDemand, the
          proxy resumes the stopped renderer and holds these requests until it runs.
  manual  For activationMode Manual: one request (the proxy answers 403 while the renderer is
          stopped), then the data plane's resume call, then the same request again until a PDF
          arrives. Reads a data-plane token from --token-file and never prints it.

Prints one JSON object. Python standard library only; followup.py uploads and runs it.

  python3 wake.py burst --url <port url> --key-file api-key --body invoice.json [--body long-table.json ...]
  python3 wake.py manual --url <port url> --key-file api-key --body invoice.json \
      --resume-url <data-plane .../sandboxes/<id>/resume?api-version=...> --token-file dp-token
"""
import argparse
import http.client
import json
import threading
import time
import urllib.error
import urllib.parse
import urllib.request


def convert(host, headers, body, timeout=180):
    connection = http.client.HTTPSConnection(host, timeout=timeout)
    started = time.monotonic()
    try:
        connection.request("POST", "/convert", body=body, headers=headers)
        response = connection.getresponse()
        data = response.read()
        status = response.status
    except (http.client.HTTPException, OSError) as error:
        data, status = str(error).encode(), 0
    finally:
        connection.close()
    seconds = time.monotonic() - started
    is_pdf = data[:5] == b"%PDF-"
    return {"status": status, "seconds": round(seconds, 3), "bytes": len(data), "pdf": is_pdf,
            "detail": "" if is_pdf else data[:120].decode(errors="replace")}


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("mode", choices=["burst", "manual"])
    parser.add_argument("--url", required=True)
    parser.add_argument("--key-file", required=True)
    parser.add_argument("--body", action="append", required=True)
    parser.add_argument("--resume-url")
    parser.add_argument("--token-file")
    options = parser.parse_args()
    host = urllib.parse.urlparse(options.url).netloc
    headers = {"Content-Type": "application/json", "X-Reports-Api-Key": open(options.key_file).read().strip()}
    bodies = [(name, open(name, "rb").read()) for name in options.body]

    if options.mode == "burst":
        results = [None] * len(bodies)

        def send(index):
            results[index] = dict(convert(host, headers, bodies[index][1]), body=bodies[index][0])

        threads = [threading.Thread(target=send, args=(i,)) for i in range(len(bodies))]
        started = time.monotonic()
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        pdfs = [r["seconds"] for r in results if r["pdf"]]
        print(json.dumps({"mode": "burst", "requests": results,
                          "first_pdf_s": min(pdfs) if pdfs else None,
                          "all_done_s": round(time.monotonic() - started, 3)}))
        return

    # manual: what a gateway does without OnDemand activation.
    token = open(options.token_file).read().strip()
    started = time.monotonic()
    first = convert(host, headers, bodies[0][1])
    resume_started = time.monotonic()
    request = urllib.request.Request(options.resume_url, method="POST", data=b"{}",
                                     headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            resume_status = response.status
    except urllib.error.HTTPError as error:
        resume_status = error.code
    resume_s = time.monotonic() - resume_started
    attempts = []
    while time.monotonic() - started < 90:
        attempt = convert(host, headers, bodies[0][1])
        attempts.append(attempt["status"])
        if attempt["pdf"]:
            break
        time.sleep(0.1)
    print(json.dumps({"mode": "manual", "first": first, "resume_status": resume_status,
                      "resume_call_s": round(resume_s, 3), "statuses_after_resume": attempts,
                      "first_pdf_s": round(time.monotonic() - started, 3) if attempts and attempts[-1] == 200 else None}))


if __name__ == "__main__":
    main()
