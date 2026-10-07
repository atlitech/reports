#!/usr/bin/env python3
"""Capture/apply approved digest pins without copying unreleased Dockerfile instructions.

Only identical image names and tags may cross from main into a release. A framework or OS
upgrade requires a new application release. Every external FROM must be pinned and consistent.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re

FROM = re.compile(r'^(FROM\s+)(\S+)([^\n]*)$', re.MULTILINE)
PIN = re.compile(r'(?P<image>[a-zA-Z0-9./:_-]+)@(?P<digest>sha256:[0-9a-f]{64})')


def capture(source):
    pins, stages = {}, set()
    for match in FROM.finditer(source):
        reference, suffix = match[2], match[3]
        pin = PIN.fullmatch(reference)
        if pin:
            name, digest = pin['image'], pin['digest']
            if name in pins and pins[name] != digest:
                raise ValueError(f'Inconsistent FROM pins for {name}')
            pins[name] = digest
        elif reference not in stages:
            raise ValueError(f'External FROM must be a literal tag@sha256 digest: {reference}')
        stage = re.fullmatch(r'\s+[Aa][Ss]\s+(\S+)\s*', suffix)
        if stage:
            stages.add(stage[1])
    if not pins:
        raise ValueError('No pinned base images found')
    return pins


def serialized(pins):
    return json.dumps(pins, sort_keys=True, separators=(',', ':'))


def fingerprint(pins):
    return hashlib.sha256(serialized(pins).encode()).hexdigest()


def apply(source, approved):
    current = capture(source)
    if set(current) != set(approved):
        raise ValueError('Approved base-image names/tags differ from the release; publish a new release for framework or OS upgrades')
    for name, digest in approved.items():
        if not isinstance(digest, str) or not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
            raise ValueError(f'Invalid approved digest for {name}')
    def replace(match):
        pin = PIN.fullmatch(match[2])
        return (match[1] + pin['image'] + '@' + approved[pin['image']] + match[3]) if pin else match[0]
    return FROM.sub(replace, source)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['capture', 'fingerprint', 'apply'])
    parser.add_argument('dockerfile', type=Path)
    parser.add_argument('--pins', default='')
    args = parser.parse_args()
    source = args.dockerfile.read_text()
    if args.command == 'apply':
        if args.pins:
            source = apply(source, json.loads(args.pins))
            args.dockerfile.write_text(source)
        print(fingerprint(capture(source)))
    elif args.command == 'capture':
        print(serialized(capture(source)))
    else:
        print(fingerprint(capture(source)))


if __name__ == '__main__':
    main()
