#!/usr/bin/env python3
"""Select expensive dependency checks from changed paths; docs-only PRs still finish the gate."""
import subprocess
import sys


def select(paths):
    runtime = any(p.startswith(('src/', 'examples/', '.github/', 'deploy/', 'scripts/')) or p in
                  ('Directory.Build.props', 'Directory.Packages.props', 'global.json', 'Atli.Reports.slnx')
                  or p.startswith('tests/Atli.Reports.AppHost.Tests/') for p in paths)
    return {'runtime': runtime,
            'devcontainer': any(p.startswith('.devcontainer/') for p in paths),
            'benchmark': 'benchmarks/load/compose.yaml' in paths}


if __name__ == '__main__':
    paths = subprocess.check_output(['git', 'diff', '--name-only', '-z', sys.argv[1], 'HEAD']).decode().split('\0')
    for key, value in select(paths).items():
        print(f'{key}={str(value).lower()}')
