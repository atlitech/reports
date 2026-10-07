#!/usr/bin/env python3
"""Write Aspire deployment credentials without printing them or replacing existing files."""
import argparse
import base64
import hashlib
import os
from pathlib import Path
import secrets


def create(path):
    values = {}
    for name, key_id in (("reports", "application"), ("provisioner", "gateway")):
        credential = f"{key_id}.{secrets.token_hex(32)}"
        values[f"Parameters__{name}Key"] = credential
        values[f"Parameters__{name}KeyHash"] = base64.b64encode(
            hashlib.sha256(credential.encode("ascii")).digest()
        ).decode("ascii")
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as output:
        output.write("# Private Aspire parameters. Do not commit this file.\n")
        for name, value in values.items():
            output.write(f"{name}={value}\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?", type=Path, default=Path(".reports-secrets/hosted.env"))
    args = parser.parse_args()
    try:
        create(args.path)
    except FileExistsError:
        parser.exit(2, f"Refusing to overwrite {args.path}; choose a new path for rotation.\n")
    print(f"Created {args.path} with owner-only access. Keep this file private.")


if __name__ == "__main__":
    main()
