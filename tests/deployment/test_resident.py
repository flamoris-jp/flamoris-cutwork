import contextlib
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
TOKEN = 'testOnlyCredential' + 'x' * 48


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result)
    return result


RESIDENT = module('resident', 'product/packaging/updater-entrypoint.py')
PACKAGE = module('package', 'product/packaging/package-updater.py')
MIGRATION = module('migration', 'deploy/linux/migrate-resident.py')
SETUP = module('setup', 'deploy/linux/setup-cutwork-lime.py')


class ResidentTests(unittest.TestCase):
    def test_installed_hub_yaml_writer_preserves_scientific_numeric_constraints(self):
        raw = {'id': 'cutwork-lime', 'namespace': 'cutwork-lime', 'tools': [
            {'name': 'mcp.context', 'input_schema': {'type': 'object', 'minimum': 1e-7}}]}
        class Config:
            @staticmethod
            def model_validate(value):
                return types.SimpleNamespace(tools=value['tools'])
        configuration = types.ModuleType('flamoris_mcp_hub.config')
        configuration.UpstreamConfig = Config
        configuration.load_upstreams = lambda _: []
        configuration.config_dir = lambda: None
        output = io.StringIO()
        with patch.dict(sys.modules, {'flamoris_mcp_hub.config': configuration}), \
             patch.object(sys, 'stdin', io.StringIO(json.dumps(raw))), contextlib.redirect_stdout(output):
            exec(compile(SETUP.VALIDATE, 'installed-yaml-validator', 'exec'), {})
        import yaml
        decoded = yaml.safe_load(json.loads(output.getvalue())['yaml'])
        self.assertEqual(decoded, raw)
        self.assertIsInstance(decoded['tools'][0]['input_schema']['minimum'], float)

    def test_settings_retain_identity_and_refuse_insecure_or_wrong_routes(self):
        with tempfile.TemporaryDirectory() as directory:
            state = pathlib.Path(directory)
            connection = state / 'connection.json'
            expected = ('wss://hub.example/prefix/desktop/cutwork-lime', TOKEN)
            connection.write_text(json.dumps(dict(zip(('endpoint', 'credential'), expected))))
            connection.chmod(0o600)
            self.assertEqual(RESIDENT.settings({}, state), expected)
            self.assertEqual(RESIDENT.settings({'CUTWORK_HUB_TOKEN': ''}, state), expected)
            for endpoint in ['ws://hub.example/desktop/cutwork-lime', 'wss://hub.example/desktop/kachinco-lime',
                             'wss://name:secret@hub.example/desktop/cutwork-lime',
                             'wss://hub.example/desktop/cutwork-lime?credential=bad']:
                with self.assertRaises(ValueError):
                    RESIDENT.settings({'CUTWORK_HUB_ENDPOINT': endpoint}, state)
            connection.chmod(0o644)
            with self.assertRaises(ValueError):
                RESIDENT.settings({}, state)

    def test_catalog_is_updater_owned_with_private_env_and_retained_state(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            archive = root / 'input.tar'; archive.write_bytes(b'isolated image fixture')
            recipe = PACKAGE.package(archive, 'sha256:' + '0' * 64, '1' * 40, root / 'output')
            self.assertEqual(recipe['application_id'], 'flamoris-cutwork-cli')
            self.assertEqual(recipe['generated']['runtime.env'].splitlines(), [
                'CUTWORK_PORT=${PORT}', 'CUTWORK_HUB_ENDPOINT=${HUB_ENDPOINT}', 'CUTWORK_HUB_TOKEN=${HUB_TOKEN}'])
            application = recipe['application']
            self.assertEqual(application['kind'], 'docker')
            self.assertEqual(application['network'], 'host')
            self.assertEqual(application['health_url'], 'http://127.0.0.1:${PORT}/healthz')
            self.assertEqual(application['mounts'], [{'source': '${root}/state', 'target': '/data/state', 'read_only': False}])
            self.assertTrue(application['files'][0]['source']['private'])
            self.assertEqual(recipe['downloads']['image.tar']['digest'], 'sha256:' + hashlib.sha256(archive.read_bytes()).hexdigest())

    def test_inventory_rejects_links_before_any_ownership_change(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / 'external').symlink_to('/etc/passwd')
            with self.assertRaises(ValueError):
                MIGRATION.inventory(root)

    def test_migration_retains_content_backup_and_rolls_back_a_failed_disable(self):
        for fail in (False, True):
            with self.subTest(fail=fail), tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory)
                state = root / 'state'; state.mkdir(mode=0o700)
                (state / 'projects').mkdir(mode=0o700)
                document = state / 'projects/saved.flimg'; document.write_bytes(b'existing authored work')
                document.chmod(0o640)
                env = root / 'native.env'
                env.write_text('HUB_ENDPOINT=wss://hub.example/desktop/cutwork-lime\n' + SETUP.KEY + '=' + TOKEN + '\n')
                unit = root / 'original.service'
                unit.write_bytes((ROOT / 'deploy/linux/flamoris-cutwork-cli.service').read_bytes())
                calls = []
                def run(arguments, check=True):
                    calls.append(arguments)
                    if arguments[:2] == ['docker', 'inspect']:
                        return types.SimpleNamespace(returncode=1)
                    if fail and arguments[:2] == ['systemctl', 'disable']:
                        raise RuntimeError('injected disable failure')
                    return types.SimpleNamespace(returncode=0)
                output = io.StringIO()
                with patch.object(MIGRATION, 'ROOT', root), patch.object(MIGRATION, 'SETTING', env), \
                     patch.object(MIGRATION, 'UNIT_PATH', unit), patch.object(MIGRATION, 'run', run), \
                     patch.object(os, 'chown'), contextlib.redirect_stdout(output):
                    if fail:
                        with self.assertRaisesRegex(RuntimeError, 'ownership/service restored'):
                            MIGRATION.prepare(SETUP)
                    else:
                        MIGRATION.prepare(SETUP)
                self.assertNotIn(TOKEN, output.getvalue())
                self.assertEqual(document.read_bytes(), b'existing authored work')
                self.assertTrue(unit.exists()); self.assertTrue(env.exists())
                backups = list(root.glob('native-backup-*'))
                self.assertEqual(len(backups), 1)
                self.assertEqual((backups[0] / 'state/projects/saved.flimg').read_bytes(), document.read_bytes())
                if fail:
                    self.assertFalse((root / 'config/native-migration.json').exists())
                    self.assertFalse((state / 'connection.json').exists())
                    self.assertEqual(document.stat().st_mode & 0o777, 0o640)
                    self.assertIn(['systemctl', 'start', MIGRATION.UNIT], calls)
                else:
                    self.assertTrue((root / 'config/native-migration.json').exists())
                    self.assertEqual(json.loads((state / 'connection.json').read_text())['credential'], TOKEN)

    def test_migration_defaults_to_plan(self):
        result = subprocess.run([sys.executable, str(ROOT / 'deploy/linux/migrate-resident.py'), 'prepare'],
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 0)
        self.assertFalse(json.loads(result.stdout)['changes'])
