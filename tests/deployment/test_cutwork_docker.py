"""Opt-in real Docker tests: no production image, catalog or credentials."""
import importlib.util
import json
import os
import pathlib
import secrets
import tempfile
import unittest

SOURCE = pathlib.Path(__file__).resolve().parents[2] / 'deploy/linux/setup-cutwork-lime.py'
SPEC = importlib.util.spec_from_file_location('cutwork_docker_setup', SOURCE)
SETUP = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SETUP)
TOKEN = 'integrationOnly' + 'x' * 48


@unittest.skipUnless(os.environ.get('CUTWORK_SETUP_DOCKER_TESTS') == '1', 'requires isolated Docker test runner')
class DockerIntegrationTests(unittest.TestCase):
    def prepare(self, swap):
        try:
            swap.prepare()
        except RuntimeError:
            if swap.candidate:
                found = SETUP.docker_api('GET', '/containers/' + swap.candidate + '/json')
                before, after = self.original['HostConfig'], found['HostConfig']
                differences = {key: [before.get(key), after.get(key)] for key in before.keys() | after.keys()
                               if before.get(key) != after.get(key)}
                if differences:
                    self.fail('Isolated fixture HostConfig differences: ' + json.dumps(differences))
            raise

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        root = pathlib.Path(self.directory.name)
        root.chmod(0o755)
        (root / 'retained.txt').write_text('original catalog data')
        self.created = []
        self.addCleanup(self.cleanup)
        spec = {'Image': 'python:3.12-slim', 'User': '10001:10001', 'WorkingDir': '/tmp',
                'Env': ['EXISTING_TOKEN=integration-placeholder', 'PORT=8769'],
                'Cmd': ['python', '-c', 'import time; time.sleep(3600)'],
                'Labels': {'flamoris.updater.operation': 'isolated-test'},
                'HostConfig': {'ReadonlyRootfs': True, 'NetworkMode': 'host',
                               'RestartPolicy': {'Name': 'always'}, 'CapDrop': ['ALL'],
                               'SecurityOpt': ['no-new-privileges:true'], 'PidsLimit': 64,
                               'Memory': 128 * 1024 * 1024,
                               'Tmpfs': {'/tmp': 'rw,noexec,nosuid,nodev,size=268435456,mode=1777'},
                               'Mounts': [{'Type': 'bind', 'Source': str(root),
                                           'Target': '/catalog', 'ReadOnly': True}]}}
        name = 'cutwork-setup-test-' + secrets.token_hex(6)
        identity = SETUP.docker_api('POST', '/containers/create?name=' + name, spec)['Id']
        self.created.append(identity)
        SETUP.docker_api('POST', '/containers/' + identity + '/start')
        self.original = SETUP.docker_api('GET', '/containers/' + identity + '/json')

    def cleanup(self):
        for identity in reversed(self.created):
            SETUP.run(['docker', 'rm', '--force', identity], check=False)
        self.directory.cleanup()

    def test_real_readonly_replacement_retains_mounts_hardening_and_other_credentials(self):
        failed_write = SETUP.run(['docker', 'exec', '--user', '0', self.original['Id'],
                                 'python', '-c', "open('/new.env','w').write('blocked')"], check=False)
        self.assertNotEqual(failed_write.returncode, 0)
        self.assertIn(b'Read-only file system', failed_write.stderr)
        swap = SETUP.HubSwap(self.original, TOKEN)
        try:
            self.prepare(swap)
        finally:
            if swap.candidate:
                self.created.append(swap.candidate)
        swap.activate()
        current = SETUP.docker_api('GET', '/containers/' + swap.candidate + '/json')
        self.assertEqual(current['HostConfig'], self.original['HostConfig'])
        self.assertEqual(current['Image'], self.original['Image'])
        result = SETUP.run(['docker', 'exec', swap.candidate, 'python', '-c',
                            "import json,os; print(json.dumps({'old':os.environ['EXISTING_TOKEN'],"
                            "'new':os.environ['CUTWORK_LIME_CONNECTOR_TOKEN'],"
                            "'data':open('/catalog/retained.txt').read()}))"])
        self.assertEqual(json.loads(result.stdout), {'old': 'integration-placeholder',
                         'new': TOKEN, 'data': 'original catalog data'})
        previous = SETUP.docker_api('GET', '/containers/' + self.original['Id'] + '/json')
        self.assertFalse(previous['State']['Running'])
        self.assertEqual(previous['HostConfig']['RestartPolicy']['Name'], 'no')
        swap.rollback()
        restored = SETUP.docker_api('GET', '/containers/' + self.original['Id'] + '/json')
        self.assertTrue(restored['State']['Running'])
        self.assertEqual(restored['Name'], self.original['Name'])
        self.assertEqual(restored['HostConfig'], self.original['HostConfig'])
        self.assertEqual(restored['Config'], self.original['Config'])

    def test_real_failed_start_restores_original_container(self):
        def fail_start(method, path, body=None):
            if path.endswith('/start') and self.original['Id'] not in path:
                raise RuntimeError('injected replacement startup failure')
            return SETUP.docker_api(method, path, body)
        swap = SETUP.HubSwap(self.original, TOKEN, fail_start)
        try:
            self.prepare(swap)
        finally:
            if swap.candidate:
                self.created.append(swap.candidate)
        with self.assertRaises(RuntimeError):
            swap.activate()
        swap.rollback()
        restored = SETUP.docker_api('GET', '/containers/' + self.original['Id'] + '/json')
        self.assertTrue(restored['State']['Running'])
        self.assertEqual(restored['Name'], self.original['Name'])
        self.assertEqual(restored['Config'], self.original['Config'])
        self.assertEqual(restored['HostConfig'], self.original['HostConfig'])
