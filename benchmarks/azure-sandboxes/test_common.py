"""Local stdlib tests. All command execution and HTTP requests use in-memory stubs."""
import concurrent.futures
import contextlib
import importlib
import io
import json
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

# Fail immediately if importing a runner ever starts a command or contacts a service.
with patch('subprocess.run', side_effect=AssertionError('CLI call during import')), \
     patch('subprocess.Popen', side_effect=AssertionError('process during import')), \
     patch('urllib.request.urlopen', side_effect=AssertionError('HTTP call during import')):
    import common
    import production
    import workspaces
    import e2e


class FakeCloud:
    def __init__(self):
        self.commands, self.requests, self.arm_files = [], [], []
        self.disk_name = None

    def run(self, command, **kwargs):
        self.commands.append((command, kwargs))
        output = {}
        if command[:3] == ['az', 'account', 'show']:
            output = {'id': 'test-subscription', 'tenantId': 'test-tenant', 'name': 'Atli'}
        elif command[:3] == ['az', 'account', 'get-access-token']:
            output = {'accessToken': 'test-access-token', 'expires_on': 10000}
        elif command[:2] == ['az', 'rest']:
            if '--body' in command:
                path = Path(command[command.index('--body') + 1][1:])
                self.arm_files.append((path, path.stat().st_mode, json.loads(path.read_text())))
        elif command[:5] == ['az', 'network', 'public-ip', 'show', '-g']:
            return subprocess.CompletedProcess(command, 0, '203.0.113.17\n', '')
        elif command[:5] == ['az', 'network', 'vnet', 'subnet', 'create']:
            output = {'id': '/test-subnet/' + command[command.index('-n') + 1]}
        elif command[:2] == ['az', 'network']:
            pass
        elif command[:4] == ['aca', 'sandboxgroup', 'disk', 'create']:
            self.disk_name = command[command.index('--name') + 1]
        elif command[:4] == ['aca', 'sandboxgroup', 'disk', 'list']:
            output = [{'id': 'test-disk', 'name': self.disk_name}]
        elif command[:3] == ['aca', 'sandboxgroup', 'create']:
            pass
        elif command[:4] == ['aca', 'sandboxgroup', 'network', 'create']:
            pass
        elif command[:3] == ['aca', 'sandbox', 'list']:
            output = []
        else:
            raise AssertionError('Unexpected stub command')
        return subprocess.CompletedProcess(command, 0, json.dumps(output), '')

    def open(self, request, **kwargs):
        self.requests.append(request)
        body = io.BytesIO(b'{"state":"Running"}')
        body.status = 200
        return body

    def runtime(self, **kwargs):
        return common.Runtime(command_runner=self.run, opener=self.open, clock=lambda: 1000, **kwargs)


class TokenTests(unittest.TestCase):
    def test_actual_expiry_and_refresh_margin(self):
        now, calls = [1000], []
        def acquire(resource):
            calls.append(resource)
            return {'accessToken': f'test-token-{len(calls)}', 'expires_on': 2000}
        cache = common.TokenCache(acquire, lambda *_: None, lambda: now[0])
        self.assertEqual(cache.get('dp', 'token'), 'test-token-1')
        now[0] = 1699
        self.assertEqual(cache.get('dp', 'token'), 'test-token-1')
        now[0] = 1700
        self.assertEqual(cache.get('dp', 'token'), 'test-token-2')
        # A fresh CLI acquisition can itself be near expiry: do not assume another 30 minutes.
        self.assertEqual(cache.get('dp', 'token'), 'test-token-3')

    def test_audiences_are_cached_separately_and_missing_expiry_is_not_cached(self):
        calls = []
        def acquire(resource):
            calls.append(resource)
            return {'accessToken': resource, **({'expires_on': 2000} if resource != 'unknown' else {})}
        cache = common.TokenCache(acquire, lambda *_: None, lambda: 1000)
        for audience in ['data-plane', 'vault', 'data-plane', 'unknown', 'unknown']:
            self.assertEqual(cache.get(audience, audience), audience)
        self.assertEqual(calls, ['data-plane', 'vault', 'unknown', 'unknown'])

    def test_concurrent_cache_misses_acquire_once(self):
        calls = []
        def acquire(resource):
            calls.append(resource)
            return {'accessToken': 'test-token', 'expires_on': 2000}
        cache = common.TokenCache(acquire, lambda *_: None, lambda: 1000)
        with concurrent.futures.ThreadPoolExecutor(8) as pool:
            tokens = list(pool.map(lambda _: cache.get('dp', 'token'), range(32)))
        self.assertEqual(tokens, ['test-token'] * 32)
        self.assertEqual(calls, ['dp'])


class RedactionTests(unittest.TestCase):
    def test_overlapping_values_and_old_tokens_remain_redacted(self):
        redact = common.Redactor()
        redact.sensitive('short', 'secret')
        redact.sensitive('long', 'secret-value')
        redact.sensitive('token', 'old-token')
        redact.sensitive('token', 'new-token')
        self.assertEqual(redact('secret-value secret old-token new-token'),
                         '<long> <short> <token> <token 2>')
        jwt = 'eyJ' + 'a' * 12 + '.' + 'b' * 12 + '.' + 'c' * 12
        credential = 'reports-' + 'a' * 12 + '.' + 'b' * 64
        self.assertEqual(redact(jwt + ' ' + credential), '<token> <api key>')

    def test_runtime_policies_do_not_modify_other_scenarios(self):
        plain = common.Redactor()
        private = common.Redactor(public_addresses=True)
        value = 'https://renderer.adcproxy.io/convert 203.0.113.17 10.42.0.4 168.63.129.16'
        self.assertEqual(plain(value), value)
        self.assertEqual(private(value), '<port URL> <ip> 10.42.0.4 168.63.129.16')
        self.assertIsNot(production.runtime, workspaces.runtime)
        before = production.redact(value)
        importlib.reload(workspaces)
        self.assertEqual(production.redact(value), before)
        self.assertEqual(workspaces.redact(value), private(value))

    def test_command_errors_are_redacted(self):
        def fail(command, **kwargs):
            return subprocess.CompletedProcess(command, 1, '', 'failure with secret-value')
        runtime = common.Runtime(command_runner=fail)
        runtime.sensitive('credential', 'secret-value')
        with self.assertRaisesRegex(RuntimeError, '<credential>') as error:
            runtime.run(['test', 'command'])
        self.assertNotIn('secret-value', str(error.exception))


class ContextTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.cloud = FakeCloud()
        self.runtime = self.cloud.runtime(public_addresses=True)
        options = SimpleNamespace(work=Path(self.folder.name) / 'work',
                                  output=Path(self.folder.name) / 'results.json', location='test-region')
        self.ctx = common.Context(options, self.runtime)
        self.ctx.state.save(resource_group='test-group', group_rend='test-renderers')

    def test_context_passes_runtime_to_http_cli_and_redacted_state(self):
        aca, rest = self.ctx.aca('rend'), self.ctx.rest('rend')
        self.assertIs(aca.runtime, self.runtime)
        self.assertIs(rest.runtime, self.runtime)
        aca('sandbox', 'list')
        command, kwargs = self.cloud.commands[-1]
        self.assertEqual(kwargs['env']['ACA_SANDBOX_GROUP'], 'test-renderers')
        self.assertEqual(rest.request('GET', 'sandboxes/one'), (200, {'state': 'Running'}))
        request = self.cloud.requests[-1]
        self.assertEqual(request.get_header('Authorization'), 'Bearer test-access-token')
        self.ctx.record('sample', {'subscription': self.ctx.subscription, 'ip': '203.0.113.17'})
        output = json.loads(self.ctx.output.read_text())['sample']
        self.assertEqual(output, {'subscription': '<subscription>', 'ip': '<ip>'})
        for file in (self.ctx.output, self.ctx.state.path, self.ctx.secret.path):
            self.assertEqual(stat.S_IMODE(file.stat().st_mode), 0o600)
        self.assertEqual(stat.S_IMODE(self.ctx.work.stat().st_mode), 0o700)

    def test_arm_body_uses_context_private_directory_and_is_removed(self):
        self.ctx.arm('PUT', '/test', {'private': 'test-secret'})
        path, mode, body = self.cloud.arm_files[-1]
        self.assertEqual(path.parent, self.ctx.work)
        self.assertEqual(stat.S_IMODE(mode), 0o600)
        self.assertEqual(body, {'private': 'test-secret'})
        self.assertFalse(path.exists())

    def test_shared_network_and_sandbox_setup_are_idempotent(self):
        with contextlib.redirect_stdout(io.StringIO()):
            common.setup_networks(self.ctx, 'test-purpose')
            common.create_sandbox_group(self.ctx, 'rend')
            common.connect_renderer_network(self.ctx, 'rend')
            self.assertEqual(self.ctx.state['network_connection'], 'renderers')
            self.assertEqual(self.ctx.secret['nat_ip'], '203.0.113.17')
            self.assertEqual(self.runtime.redact('203.0.113.17'), '<nat ip>')
            self.cloud.commands.clear()
            common.setup_networks(self.ctx, 'test-purpose')
            common.create_sandbox_group(self.ctx, 'rend')
            common.connect_renderer_network(self.ctx, 'rend')
        # Reruns only refresh the public IP; no resources are created again.
        self.assertEqual(len(self.cloud.commands), 1)
        self.assertEqual(self.cloud.commands[0][0][:4], ['az', 'network', 'public-ip', 'show'])

    def test_disk_builder_gets_its_own_context_and_cleans_it(self):
        built = []
        def build(work):
            built.append(work)
            context = work / 'context'
            context.mkdir()
            return context
        disk, seconds = common.build_sandbox_disk(self.ctx, 'rend', 'test-image', build)
        self.assertEqual(disk, 'test-disk')
        self.assertGreaterEqual(seconds, 0)
        self.assertFalse(built[0].exists())
        self.assertEqual(built[0].parent, self.ctx.work)


class ScenarioTests(unittest.TestCase):
    def test_e2e_verdict_checks_concurrent_on_demand_wake(self):
        pdf = {'status': 200, 'pdf': True}
        result = {'auto_suspend': {'globex': {}}, 'c_concurrent_ondemand_wake': {
            'invoices': [pdf] * 2, 'state_after': 'Running', 'invoice_warm': pdf}}
        self.assertEqual(e2e.verdicts(result)['c_concurrent_ondemand_wake'], 'pass')
        result['c_concurrent_ondemand_wake']['invoices'] = [pdf, {'status': 503}]
        self.assertEqual(e2e.verdicts(result)['c_concurrent_ondemand_wake'], 'FAIL')

    def test_active_gateway_configuration_has_no_manual_wake_settings(self):
        ctx = SimpleNamespace(state={'vault_uri': 'https://test.vault.azure.net'},
                              secret={'identity_client_id': 'test-identity'})
        settings = production.gateway_env(ctx, False, False)
        self.assertFalse(any('__Wake__' in name for name in settings))
        self.assertFalse(any('Provisioner__PortActivation' in overrides
                             for _, _, overrides in production.TENANTS.values()))
        # The old role/Manual-port measurements live in historical results, not active runners.
        for module in (production, workspaces, e2e):
            source = Path(module.__file__).read_text()
            self.assertNotIn('ReportsServer__Gateway__Wake__', source)
            self.assertNotIn('Provisioner__PortActivation', source)
            self.assertNotIn('"role", "definition", "create"', source)


if __name__ == '__main__':
    unittest.main()
