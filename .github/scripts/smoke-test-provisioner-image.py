#!/usr/bin/env python3
"""Smoke-test the real chiseled image without Azure credentials or creating cloud resources."""
import base64
import hashlib
import http.client
import json
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request


def docker(*args):
    return subprocess.check_output(['docker', *args], text=True).strip()


def main():
    image = sys.argv[1]
    key = 'smoke.' + secrets.token_hex(32)
    settings = {
        'Provisioner__Sandboxes__SubscriptionId': '00000000-0000-0000-0000-000000000000',
        'Provisioner__Sandboxes__ResourceGroup': 'smoke',
        'Provisioner__Sandboxes__SandboxGroup': 'smoke',
        'Provisioner__Sandboxes__Region': 'eastus2',
        'Provisioner__Records__Store': 'File',
        'Provisioner__Records__Path': '/records',
        'Provisioner__DiskImageId': 'smoke',
        'Provisioner__AllowedSourceCidrs__0': '127.0.0.1/32',
        'Provisioner__Service__TenantPrefixes__0__Prefix': 'smoke-',
        'Provisioner__Service__ApiKeys__0__Id': 'smoke',
        'Provisioner__Service__ApiKeys__0__Hash': base64.b64encode(hashlib.sha256(key.encode()).digest()).decode(),
        'Provisioner__Service__RetireAfterIdle': '00:00:00',
    }
    args = ['run', '-d', '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges',
            '--tmpfs', '/tmp:rw,noexec,nosuid', '--tmpfs', '/records:rw,noexec,nosuid,uid=1654,gid=1654,mode=0700',
            '-p', '127.0.0.1::8080']
    for name, value in settings.items():
        args += ['-e', f'{name}={value}']
    container = docker(*args, image)
    try:
        port = docker('port', container, '8080/tcp').split(':')[-1]
        base = 'http://127.0.0.1:' + port
        def request(path, credential=None, method='GET'):
            headers = {'X-Reports-Api-Key': credential} if credential else {}
            try:
                with urllib.request.urlopen(urllib.request.Request(base + path, headers=headers, method=method), timeout=3) as response:
                    return response.status
            except urllib.error.HTTPError as error:
                return error.code
        for _ in range(60):
            try:
                if request('/health/ready') == 200:
                    break
            except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.HTTPException):
                pass
            if docker('inspect', '--format', '{{.State.Running}}', container) != 'true':
                raise RuntimeError('Provisioner exited before readiness')
            time.sleep(1)
        else:
            raise RuntimeError('Provisioner never became ready')
        assert request('/health/live') == 200
        assert request('/tenants/other-one/renderer', method='PUT') == 401
        assert request('/tenants/other-one/renderer', 'smoke.invalid', 'PUT') == 401
        # A valid key must still be forbidden from provisioning outside its configured prefixes.
        assert request('/tenants/other-one/renderer', key, 'PUT') == 403
        user = docker('inspect', '--format', '{{.Config.User}}', container)
        assert user == '1654', f'Unexpected user: {user}'
        docker('stop', '--time', '15', container)
        state = json.loads(docker('inspect', '--format', '{{json .State}}', container))
        assert state['ExitCode'] == 0 and not state['OOMKilled'], state
        print('Provisioner: readiness, authentication, tenant boundary, non-root, and graceful shutdown passed')
    finally:
        subprocess.run(['docker', 'logs', container], check=False)
        subprocess.run(['docker', 'rm', '-f', container], check=False)


if __name__ == '__main__':
    main()
