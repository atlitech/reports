#!/usr/bin/env python3
"""Propose stable custom-pin updates. Network reads are separate from validated file edits.

Use --write in the scheduled workflow; without it, report the available update without editing.
Checksums are verified against the downloaded assets before proposing a version change.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[2]


def download(url):
    headers = {"User-Agent": "atlitech-reports-dependency-updater"}
    # Never forward the GitHub token to asset storage or a different host.
    if urllib.parse.urlparse(url).netloc == "api.github.com" and os.getenv("GH_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GH_TOKEN"]
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=120) as response:
        return response.read()


def api(path):
    return json.loads(download("https://api.github.com/" + path))


def version(value):
    if not re.fullmatch(r"v?\d+\.\d+\.\d+", value):
        raise ValueError(f"Not a stable version: {value}")
    return tuple(map(int, value.removeprefix("v").split(".")))


def latest_release(repo, current):
    releases = api(f"repos/{repo}/releases?per_page=100")
    candidates = [r["tag_name"] for r in releases if not r["draft"] and not r["prerelease"]
                  and re.fullmatch(r"v?\d+\.\d+\.\d+", r["tag_name"])
                  and version(r["tag_name"])[0] == version(current)[0]]
    if not candidates:
        raise ValueError(f"No stable releases for {repo} in major {version(current)[0]}")
    return max(candidates, key=version)


def replace_once(text, pattern, replacement):
    result, count = re.subn(pattern, lambda _: replacement, text)
    if count != 1:
        raise ValueError(f"Expected one match for {pattern!r}, found {count}")
    return result


def checksum_list(body):
    result = {}
    for line in body.decode().splitlines():
        match = re.fullmatch(r"([0-9a-fA-F]{64})\s+\*?(?:\./)?([^/\s]+)", line)
        if match:
            digest, name = match.groups()
            if name in result:
                raise ValueError(f"Duplicate checksum: {name}")
            result[name] = digest.lower()
    return result


def verified_assets(repo, tag, names, checksum_file):
    base = f"https://github.com/{repo}/releases/download/{tag}/"
    checksums = checksum_list(download(base + checksum_file))
    for name in names:
        if name not in checksums:
            raise ValueError(f"Missing checksum for {name}")
        if hashlib.sha256(download(base + name)).hexdigest() != checksums[name]:
            raise ValueError(f"Checksum mismatch for {name}")
    return {name: checksums[name] for name in names}


def tailwind_edits(source, props, target, digests):
    current = re.search(r'<AtliTailwindVersion[^>]*>([\d.]+)</AtliTailwindVersion>', props)[1]
    assets = re.findall(r'\["(tailwindcss-[^"]+)"\]\s*=\s*"[0-9a-f]{64}"', source)
    if not assets or set(assets) != set(digests):
        raise ValueError("Every supported Tailwind platform must have a verified checksum")
    if version(target) <= version(current) or version(target)[0] != 4:
        raise ValueError("Tailwind updates must advance within stable v4")
    # All occurrences in this file are the default, its checksum selector, or documentation.
    source = source.replace(current, target)
    for asset in assets:
        pattern = r'(\["' + re.escape(asset) + r'"\]\s*=\s*")[0-9a-f]{64}(",)'
        source, count = re.subn(pattern, lambda m: m[1] + digests[asset] + m[2], source)
        if count != 1:
            raise ValueError(f"Expected one pinned digest for {asset}")
    return source, props.replace(f'>{current}</AtliTailwindVersion>', f'>{target}</AtliTailwindVersion>')


def update_tailwind(root):
    source_path = root / 'src/Atli.Reports.Blazor.Tailwind.Build/BuildTailwindCss.cs'
    props_path = root / 'src/Atli.Reports.Blazor.Tailwind/build/Atli.Reports.Blazor.Tailwind.props'
    source, props = source_path.read_text(), props_path.read_text()
    current = re.search(r'<AtliTailwindVersion[^>]*>([\d.]+)</AtliTailwindVersion>', props)[1]
    tag = latest_release('tailwindlabs/tailwindcss', current)
    if version(tag) <= version(current):
        return {}
    names = re.findall(r'\["(tailwindcss-[^"]+)"\]\s*=\s*"[0-9a-f]{64}"', source)
    digests = verified_assets('tailwindlabs/tailwindcss', tag, names, 'sha256sums.txt')
    source, props = tailwind_edits(source, props, tag.removeprefix('v'), digests)
    return {source_path: source, props_path: props}


def update_kind(root):
    path = root / '.github/dependency-pins.json'
    pins = json.loads(path.read_text())
    current = pins['kind']['version']
    tag = latest_release('kubernetes-sigs/kind', current)
    if version(tag) <= version(current):
        return {}
    base = f'https://github.com/kubernetes-sigs/kind/releases/download/{tag}/'
    digest = download(base + 'kind-linux-amd64.sha256sum').decode().split()[0]
    if not re.fullmatch('[0-9a-f]{64}', digest):
        raise ValueError('Invalid kind checksum')
    if hashlib.sha256(download(base + 'kind-linux-amd64')).hexdigest() != digest:
        raise ValueError('kind checksum mismatch')
    pins['kind'] = {'version': tag, 'sha256': digest}
    return {path: json.dumps(pins, indent=2) + '\n'}


def update_seccomp(root):
    path = root / '.github/scripts/chromium-seccomp-profile.py'
    source = path.read_text()
    current = re.search(r'# moby/profiles tag seccomp/(v[\d.]+)', source)[1]
    tags = api('repos/moby/profiles/tags?per_page=100')
    candidates = [t['name'] for t in tags if re.fullmatch(r'seccomp/v\d+\.\d+\.\d+', t['name'])]
    tag = max(candidates, key=lambda t: version(t.split('/')[1]))
    if version(tag.split('/')[1]) <= version(current):
        return {}
    commit = api('repos/moby/profiles/commits/' + urllib.parse.quote(tag, safe=''))['sha']
    if not re.fullmatch('[0-9a-f]{40}', commit):
        raise ValueError('Invalid upstream commit')
    body = download(f'https://raw.githubusercontent.com/moby/profiles/{commit}/seccomp/default.json')
    json.loads(body)
    source = replace_once(source, r'# moby/profiles tag seccomp/[^\n]+', f'# moby/profiles tag {tag}; changes require a security-policy review.')
    source = replace_once(source, r'UPSTREAM_COMMIT = "[0-9a-f]{40}"', f'UPSTREAM_COMMIT = "{commit}"')
    source = replace_once(source, r'UPSTREAM_SHA256 = "[0-9a-f]{64}"', f'UPSTREAM_SHA256 = "{hashlib.sha256(body).hexdigest()}"')
    return {path: source}


def update_gotenberg(root):
    path = root / 'benchmarks/load/compose.yaml'
    source = path.read_text()
    current = re.search(r'gotenberg/gotenberg:([\d.]+)-chromium@sha256:[0-9a-f]{64}', source)[1]
    tag = latest_release('gotenberg/gotenberg', current).removeprefix('v')
    if version(tag) <= version(current):
        tag = current  # Digest-only rebuilds matter too.
    image = f'gotenberg/gotenberg:{tag}-chromium'
    manifest = json.loads(subprocess.check_output(['docker', 'buildx', 'imagetools', 'inspect', image, '--format', '{{json .Manifest}}'], text=True))
    digest = manifest['digest']
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
        raise ValueError('Invalid Gotenberg image digest')
    result = replace_once(source, r'gotenberg/gotenberg:[\d.]+-chromium@sha256:[0-9a-f]{64}', f'{image}@{digest}')
    return {path: result} if result != source else {}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('dependency', choices=['tailwind', 'kind', 'seccomp', 'gotenberg'])
    parser.add_argument('--write', action='store_true')
    args = parser.parse_args()
    edits = globals()['update_' + args.dependency](ROOT)
    if not edits:
        print(f'{args.dependency}: up to date within the supported release line')
        return
    for path, content in edits.items():
        print(f'Update {path.relative_to(ROOT)}')
        if args.write:
            path.write_text(content)
    if args.write and args.dependency == 'seccomp':
        # This runs our own generator, never upstream Python. Its invariants must still pass.
        subprocess.run(['python3', str(ROOT / '.github/scripts/chromium-seccomp-profile.py'), 'write'], check=True)


if __name__ == '__main__':
    main()
