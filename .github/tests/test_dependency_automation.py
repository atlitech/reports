import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / '.github/scripts' / f'{name}.py')
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


images = module('image-inputs')
updates = module('dependency-updates')
paths = module('dependency-paths')


class ImageInputs(unittest.TestCase):
    def setUp(self):
        self.source = 'FROM registry/sdk:10.0@sha256:' + 'a' * 64 + ' AS build\nRUN echo release-code\nFROM registry/runtime:10.0@sha256:' + 'b' * 64 + '\nCOPY --from=build /app /app\n'

    def test_only_digests_change(self):
        pins = images.capture(self.source)
        pins['registry/sdk:10.0'] = 'sha256:' + 'c' * 64
        result = images.apply(self.source, pins)
        self.assertEqual(result, self.source.replace('a' * 64, 'c' * 64))
        self.assertEqual(images.capture(result), pins)

    def test_framework_and_os_migrations_are_rejected(self):
        pins = images.capture(self.source)
        pins['registry/sdk:11.0'] = pins.pop('registry/sdk:10.0')
        with self.assertRaisesRegex(ValueError, 'new release'):
            images.apply(self.source, pins)

    def test_unpinned_and_inconsistent_inputs_are_rejected(self):
        for source in ('FROM sdk:10.0\n', self.source + 'FROM registry/sdk:10.0@sha256:' + 'd' * 64):
            with self.assertRaises(ValueError):
                images.capture(source)
        pins = images.capture(self.source)
        pins['registry/sdk:10.0'] = 'bad\nRUN arbitrary'
        with self.assertRaises(ValueError):
            images.apply(self.source, pins)

    def test_fingerprint_is_independent_of_key_order(self):
        pins = images.capture(self.source)
        self.assertEqual(images.fingerprint(pins), images.fingerprint(dict(reversed(list(pins.items())))))


class CustomUpdates(unittest.TestCase):
    def test_tailwind_version_and_all_checksums_move_together(self):
        source = (ROOT / 'src/Atli.Reports.Blazor.Tailwind.Build/BuildTailwindCss.cs').read_text()
        props = (ROOT / 'src/Atli.Reports.Blazor.Tailwind/build/Atli.Reports.Blazor.Tailwind.props').read_text()
        names = updates.re.findall(r'\["(tailwindcss-[^"]+)"\]\s*=\s*"[0-9a-f]{64}"', source)
        digests = {name: 'a' * 64 for name in names}
        new_source, new_props = updates.tailwind_edits(source, props, '4.99.0', digests)
        self.assertNotIn('4.3.3', new_source)
        self.assertIn('>4.99.0</AtliTailwindVersion>', new_props)
        self.assertEqual(new_source.count('a' * 64), len(names))
        del digests[names[0]]
        with self.assertRaises(ValueError):
            updates.tailwind_edits(source, props, '4.99.0', digests)

    def test_download_checksum_mismatch_is_rejected(self):
        with patch.object(updates, 'download', side_effect=[b'a' * 64 + b'  tool\n', b'tampered']):
            with self.assertRaisesRegex(ValueError, 'Checksum mismatch'):
                updates.verified_assets('owner/repo', 'v1.0.0', ['tool'], 'sha256sums.txt')

    def test_release_selection_excludes_prereleases_and_major_upgrades(self):
        releases = [{'tag_name': name, 'draft': False, 'prerelease': pre} for name, pre in
                    [('v4.3.4', False), ('v5.0.0', False), ('v4.4.0-rc.1', True)]]
        with patch.object(updates, 'api', return_value=releases):
            self.assertEqual(updates.latest_release('owner/repo', '4.3.3'), 'v4.3.4')

    def test_dependency_changes_select_runtime_checks(self):
        for name in ['Directory.Packages.props', 'global.json', '.github/dependency-pins.json',
                     'src/Atli.Reports.Blazor.Tailwind.Build/BuildTailwindCss.cs']:
            self.assertTrue(paths.select([name])['runtime'])
        self.assertFalse(paths.select(['docs/dependencies.md'])['runtime'])
        self.assertTrue(paths.select(['.devcontainer/Atli.Reports.Dev.Dockerfile'])['devcontainer'])


class RefreshPlan(unittest.TestCase):
    def run_plan(self, chrome, bases, desired_chrome='154.0.8037.92', desired_bases='b' * 64):
        with tempfile.TemporaryDirectory() as temp:
            executable = Path(temp) / 'docker'
            executable.write_text('''#!/usr/bin/env python3
import json, os, sys
if '--raw' in sys.argv:
 print(json.dumps({'annotations': {'io.github.atlitech.reports.chrome-version': os.environ['CURRENT_CHROME'], 'io.github.atlitech.reports.base-images-sha256': os.environ['CURRENT_BASES']}}))
else:
 print(json.dumps({'digest': 'sha256:' + 'a' * 64}))
''')
            executable.chmod(0o755)
            env = {**os.environ, 'PATH': temp + os.pathsep + os.environ['PATH'], 'IMAGE': 'registry/server',
                   'CHROME_VERSION': desired_chrome, 'BASE_IMAGES_SHA': desired_bases,
                   'CURRENT_CHROME': chrome, 'CURRENT_BASES': bases}
            env.pop('GITHUB_OUTPUT', None)
            return subprocess.check_output(['bash', str(ROOT / '.github/scripts/server-image.sh'), 'refresh-plan'],
                                           input='1.0.0\tfalse\n', text=True, env=env)

    def test_base_only_change_refreshes(self):
        self.assertIn('refresh=true', self.run_plan('154.0.8037.92', 'a' * 64))

    def test_same_inputs_skip(self):
        self.assertIn('refresh=false', self.run_plan('154.0.8037.92', 'b' * 64))

    def test_never_downgrade_browser(self):
        self.assertIn('refresh=false', self.run_plan('155.0.1.1', 'a' * 64))

    def test_browser_upgrade_refreshes(self):
        self.assertIn('refresh=true', self.run_plan('153.0.1.1', 'b' * 64))

    def test_old_images_without_annotations_refresh(self):
        self.assertIn('refresh=true', self.run_plan('-', '-'))

    def test_provisioner_base_only_refresh(self):
        self.assertIn('refresh=true', self.run_plan('-', 'a' * 64, desired_chrome=''))
        self.assertIn('refresh=false', self.run_plan('-', 'b' * 64, desired_chrome=''))


class ManifestPublication(unittest.TestCase):
    def publish(self, source_char='c'):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            executable = directory / 'docker'
            executable.write_text('''#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
state = pathlib.Path(os.environ['FAKE_DOCKER_STATE'])
created = state.exists()
if 'create' in args:
 state.write_text(json.dumps(args))
 sys.exit(0)
reference = next((a for a in args if a.startswith('registry/server')), '')
if '--raw' in args:
 print(json.dumps({'annotations': {}}))
elif '-image' in reference and not created:
 print('manifest unknown', file=sys.stderr)
 sys.exit(1)
else:
 digest = 'sha256:' + ('e' if reference.endswith(':latest') else 'a') * 64
 print(json.dumps({'digest': digest}))
''')
            executable.chmod(0o755)
            env = {**os.environ, 'PATH': temp + os.pathsep + os.environ['PATH'], 'IMAGE': 'registry/server',
                   'CHROME_VERSION': '154.0.8037.92', 'VERSION': '1.2.3', 'BASE_IMAGES_SHA': 'b' * 64,
                   'TAGS': '1.2.3 latest', 'ONLY_MOVE_FROM': 'sha256:' + 'a' * 64,
                   'REVISION': 'f' * 40, 'SOURCE': 'https://github.com/owner/repo',
                   'IMAGE_TITLE': 'Server', 'IMAGE_DESCRIPTION': 'Test', 'IMAGE_LICENSES': 'Apache-2.0',
                   'FAKE_DOCKER_STATE': str(directory / 'state')}
            env.pop('GITHUB_OUTPUT', None)
            result = subprocess.check_output(['bash', str(ROOT / '.github/scripts/server-image.sh'), 'push-manifest',
                                              'registry/server@sha256:' + source_char * 64], text=True, env=env)
            args = json.loads((directory / 'state').read_text())
            return result, [args[i + 1] for i, arg in enumerate(args) if arg == '--tag']

    def test_refresh_preserves_tags_that_moved_to_another_release(self):
        output, tags = self.publish()
        self.assertIn('registry/server:1.2.3', tags)
        self.assertNotIn('registry/server:latest', tags)
        self.assertIn('Leaving registry/server:latest', output)

    def test_same_chrome_new_build_has_a_distinct_immutable_tag(self):
        _, first = self.publish('c')
        _, second = self.publish('d')
        first_immutable = next(tag for tag in first if '-image' in tag)
        second_immutable = next(tag for tag in second if '-image' in tag)
        self.assertNotEqual(first_immutable, second_immutable)
        self.assertTrue(first_immutable.startswith('registry/server:1.2.3-chrome154.0.8037.92-image'))


if __name__ == '__main__':
    unittest.main()
