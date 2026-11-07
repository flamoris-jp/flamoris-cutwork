import hashlib
import contextlib
import copy
import importlib.util
import io
import json
import os
import pathlib
import subprocess
import sys
import tarfile
import tempfile
import types
import unittest
from unittest.mock import patch

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "deploy/linux/setup-cutwork-lime.py"
SPEC = importlib.util.spec_from_file_location("cutwork_setup", SOURCE)
SETUP = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SETUP)
TOKEN = "testOnlyCredential" + "x" * 48


def running_hub():
    return {'Id': 'original', 'Name': '/flamoris-mcp-hub', 'Image': 'sha256:immutable',
            'State': {'Running': True}, 'Config': {'Env': ['EXISTING_TOKEN=untouched', 'PORT=8769'],
            'Image': 'old-tag', 'User': '10001:10001', 'WorkingDir': '/app',
            'Entrypoint': ['python'], 'Cmd': ['-m', 'hub'], 'Labels': {'owner': 'updater'}},
            'HostConfig': {'ReadonlyRootfs': True, 'NetworkMode': 'host', 'AutoRemove': False,
            'RestartPolicy': {'Name': 'always', 'MaximumRetryCount': 0}, 'Tmpfs': {'/tmp': 'rw,noexec,nosuid'},
            'CapDrop': ['ALL'], 'SecurityOpt': ['no-new-privileges'], 'Memory': 123456,
            'Mounts': [{'Type': 'bind', 'Source': '/srv/hub/mcps', 'Target': '/app/config/mcps', 'ReadOnly': True}]},
            'Mounts': [{'Type': 'bind', 'Source': '/srv/hub/mcps', 'Destination': '/app/config/mcps',
                        'RW': False, 'Propagation': 'rprivate'}]}


class FakeDocker:
    def __init__(self, original=None, fail=None):
        self.containers = {'original': copy.deepcopy(original or running_hub())}
        self.fail = fail
        self.calls = []

    def __call__(self, method, path, body=None):
        self.calls.append((method, path))
        if self.fail and self.fail in path:
            raise RuntimeError('simulated Docker failure')
        if path == '/info':
            return {'ID': 'local-daemon'}
        if path.startswith('/containers/create?'):
            item = copy.deepcopy(running_hub())
            item.update(Id='candidate', Name='/' + path.split('name=')[1], State={'Running': False})
            item['Config'] = {key: value for key, value in body.items() if key != 'HostConfig'}
            item['HostConfig'] = copy.deepcopy(body['HostConfig'])
            self.containers['candidate'] = item
            return {'Id': 'candidate'}
        identity = path.split('/')[2].split('?')[0]
        item = self.containers[identity]
        if method == 'DELETE':
            del self.containers[identity]
        elif path.endswith('/json'):
            return copy.deepcopy(item)
        elif '/stop?' in path:
            item['State']['Running'] = False
        elif '/rename?' in path:
            item['Name'] = '/' + path.split('name=')[1]
        elif path.endswith('/update'):
            item['HostConfig'].update(copy.deepcopy(body))
        elif path.endswith('/start'):
            self.assert_no_other_running(item)
            item['State']['Running'] = True
        else:
            raise AssertionError((method, path))

    def assert_no_other_running(self, target):
        assert not any(item is not target and item['State']['Running'] for item in self.containers.values())


def catalog():
    names = sorted(SETUP.READS | {"document_import", "edit", "undo", "redo", "upload_begin",
                                 "upload_write", "upload_cancel", "artifact_export", "project_save",
                                 "project_checkpoint", "project_save_cancel", "part_polygon_create",
                                 "repair_commit_candidate", "work_log_append"})
    assert len(names) == 29
    return {"id": "cutwork-lime", "namespace": "cutwork-lime", "transport": "reverse-websocket",
            "product_id": "flamoris.cutwork", "connector_token_env": SETUP.KEY,
            "tools": [{"name": name, "input_schema": {"type": "object"}} for name in names]}


class SetupTests(unittest.TestCase):
    def test_endpoint_derives_only_dedicated_route_and_preserves_front_door(self):
        self.assertEqual(SETUP.endpoint_from_existing("wss://hub.example:443/prefix/desktop/kachinco-lime"),
                         "wss://hub.example:443/prefix/desktop/cutwork-lime")
        for value in ["ws://hub.example/desktop/x", "wss://name:secret@hub.example/desktop/x",
                      "wss://hub.example/desktop/x?token=value", "wss://hub.example/desktop/x/other"]:
            with self.assertRaises(ValueError):
                SETUP.endpoint_from_existing(value)

    def test_credential_append_preserves_existing_bytes_and_is_idempotent(self):
        original = "# retained\nEXISTING_TOKEN=unrelated\nPORT=8769\n"
        updated = SETUP.append_credential(original, TOKEN)
        self.assertTrue(updated.startswith(original))
        self.assertEqual(SETUP.append_credential(updated, TOKEN), updated)
        with self.assertRaises(ValueError):
            SETUP.append_credential(updated, TOKEN + "other")
        with self.assertRaises(ValueError):
            SETUP.append_credential("PORT=1\nPORT=2\n", TOKEN)

    def test_catalog_annotations_preserve_schemas_and_do_not_grant_edits_as_reads(self):
        raw = catalog()
        result = SETUP.descriptor(raw)
        self.assertNotIn("annotations", raw["tools"][0])
        self.assertEqual([t["input_schema"] for t in raw["tools"]], [t["input_schema"] for t in result["tools"]])
        tools = {tool["name"]: tool for tool in result["tools"]}
        self.assertTrue(tools["artifact_read"]["annotations"]["readOnlyHint"])
        self.assertTrue(tools["document_import"]["annotations"]["destructiveHint"])
        raw["namespace"] = "cutwork"
        with self.assertRaises(ValueError):
            SETUP.descriptor(raw)

    def test_bad_archive_digest_and_traversal_do_not_publish_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            archive = root / "input.tar.gz"
            with tarfile.open(archive, "w:gz") as bundle:
                member = tarfile.TarInfo("../escaped")
                member.size = 4
                bundle.addfile(member, io.BytesIO(b"test"))
            destination = root / "destination"
            destination.mkdir()
            with self.assertRaises(ValueError):
                SETUP.extract_verified(archive, destination)
            digest = hashlib.sha256(archive.read_bytes()).hexdigest()
            with patch.object(SETUP, "ARCHIVE_SHA256", digest), self.assertRaises(ValueError):
                SETUP.extract_verified(archive, destination)
            self.assertEqual(list(destination.iterdir()), [])
            self.assertFalse((root / "escaped").exists())

    def test_symlinked_configuration_path_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "actual").mkdir()
            (root / "link").symlink_to(root / "actual", target_is_directory=True)
            with self.assertRaises(ValueError):
                SETUP.no_links(root / "link" / "runtime.env")

    def test_verified_stage_is_traversable_by_the_dedicated_service_account(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            archive = root / "input.tar.gz"
            with tarfile.open(archive, "w:gz") as bundle:
                member = tarfile.TarInfo("cutwork-cli")
                member.size = 4
                member.mode = 0o755
                bundle.addfile(member, io.BytesIO(b"test"))
            destination = root / "stage"
            destination.mkdir(mode=0o700)
            digest = hashlib.sha256(archive.read_bytes()).hexdigest()
            with patch.object(SETUP, "ARCHIVE_SHA256", digest):
                SETUP.extract_verified(archive, destination)
            self.assertEqual(destination.stat().st_mode & 0o777, 0o755)
            self.assertTrue((destination / "cutwork-cli").stat().st_mode & 0o111)

    def test_container_dotenv_probe_is_read_only_and_never_prints_credential(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "runtime.env"
            original = "EXISTING_TOKEN=untouched\n" + SETUP.KEY + '=' + TOKEN + '\n'
            path.write_text(original)
            path.chmod(0o400)
            configuration = types.ModuleType('flamoris_mcp_hub.config')
            configuration.env_file = lambda: path
            output = io.StringIO()
            with patch.dict(sys.modules, {'flamoris_mcp_hub.config': configuration}), \
                 patch.object(sys, 'stdin', io.StringIO(json.dumps({'key': SETUP.KEY, 'credential': TOKEN}))), \
                 contextlib.redirect_stdout(output):
                exec(compile(SETUP.CONTAINER_ENV, 'readonly-dotenv-probe', 'exec'), {})
            self.assertNotIn(TOKEN, output.getvalue())
            self.assertTrue(json.loads(output.getvalue())['validated'])
            self.assertEqual(path.read_text(), original)
            self.assertEqual(path.stat().st_mode & 0o777, 0o400)

    def test_replacement_preserves_every_setting_and_uses_immutable_image(self):
        original = running_hub()
        original['Config']['Hostname'] = 'decopon'
        spec = SETUP.replacement_spec(original, TOKEN)
        self.assertNotIn('Hostname', spec)
        self.assertEqual(spec['HostConfig'], original['HostConfig'])
        self.assertEqual(spec['Image'], original['Image'])
        self.assertEqual(spec['Env'][:-1], original['Config']['Env'])
        self.assertEqual(spec['Env'][-1], SETUP.KEY + '=' + TOKEN)
        self.assertNotIn(SETUP.KEY, original['Config']['Env'])
        for key in ('User', 'WorkingDir', 'Entrypoint', 'Cmd', 'Labels'):
            self.assertEqual(spec[key], original['Config'][key])
        for transform in (lambda x: x['HostConfig'].update(ReadonlyRootfs=False),
                          lambda x: x['HostConfig'].update(NetworkMode='bridge'),
                          lambda x: x['Mounts'][0].update(Type='volume'),
                          lambda x: x['Config']['Env'].append(SETUP.KEY + '=different')):
            unsupported = copy.deepcopy(original)
            transform(unsupported)
            with self.assertRaises(ValueError):
                SETUP.replacement_spec(unsupported, TOKEN)

    def test_container_swap_never_runs_both_and_retains_disabled_original(self):
        fake = FakeDocker()
        swap = SETUP.HubSwap(running_hub(), TOKEN, fake)
        swap.prepare()
        self.assertTrue(fake.containers['original']['State']['Running'])
        swap.activate()
        self.assertTrue(fake.containers['candidate']['State']['Running'])
        self.assertFalse(fake.containers['original']['State']['Running'])
        self.assertEqual(fake.containers['original']['HostConfig']['RestartPolicy']['Name'], 'no')
        swap.rollback()
        self.assertEqual(fake.containers, {'original': running_hub()})

    def test_failed_replacement_start_restores_original_id_name_and_restart_policy(self):
        fake = FakeDocker(fail='/candidate/start')
        swap = SETUP.HubSwap(running_hub(), TOKEN, fake)
        swap.prepare()
        with self.assertRaises(RuntimeError):
            swap.activate()
        swap.rollback()
        self.assertEqual(fake.containers, {'original': running_hub()})

    def test_default_oom_representation_is_equivalent_but_disabling_oom_killing_is_rejected(self):
        before = running_hub()['HostConfig']
        null = dict(before, OomKillDisable=None)
        false = dict(before, OomKillDisable=False)
        true = dict(before, OomKillDisable=True)
        self.assertEqual(SETUP.comparable_host_config(null), SETUP.comparable_host_config(false))
        self.assertNotEqual(SETUP.comparable_host_config(null), SETUP.comparable_host_config(true))
        self.assertIsNone(null['OomKillDisable'])

    def test_failed_hub_verification_restores_container_descriptor_and_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "config").mkdir()
            (root / "mcps").mkdir()
            runtime = root / "config/runtime.env"
            original_env = "PORT=8769\nEXISTING_TOKEN=untouched\n"
            runtime.write_text(original_env)
            runtime.chmod(0o640)
            registration = SETUP.descriptor(catalog())
            target = root / "mcps/cutwork-lime.yaml"
            original_catalog = json.dumps(registration, separators=(",", ":")).encode()
            target.write_bytes(original_catalog)
            connection = root / "connection.json"
            connection.write_text(json.dumps({"source": SETUP.SOURCE, "archiveSha256": SETUP.ARCHIVE_SHA256,
                                             "credential": TOKEN, "descriptor": registration}))
            connection.chmod(0o600)

            fake = FakeDocker()
            def fake_run(arguments, **kwargs):
                if arguments[:2] == ["docker", "inspect"]:
                    return types.SimpleNamespace(stdout=json.dumps([running_hub()]).encode(), returncode=0)
                if arguments[:2] == ['docker', 'info']:
                    return types.SimpleNamespace(stdout=b'local-daemon\n', returncode=0)
                if arguments[-1] == SETUP.VALIDATE:
                    return types.SimpleNamespace(stdout=json.dumps({'validated': True, 'existingTools': [], 'yaml': json.dumps(registration)}).encode(), returncode=0)
                if arguments[-1] == SETUP.VERIFY:
                    raise RuntimeError('verifier transport failed')
                return types.SimpleNamespace(stdout=b"{}", returncode=0)

            with patch.object(SETUP, "HUB_ROOT", root), patch.object(SETUP, "run", fake_run), \
                 patch.object(SETUP, 'ensure_nginx_route'), \
                 patch.object(SETUP, 'docker_api', fake), \
                 patch.object(SETUP.socket, "gethostname", return_value="decopon"), self.assertRaisesRegex(RuntimeError, 'verifying the Hub catalog'):
                SETUP.hub(types.SimpleNamespace(connection=str(connection)), types.SimpleNamespace(pw_dir=str(root)))
            self.assertEqual(runtime.read_text(), original_env)
            self.assertEqual(runtime.stat().st_mode & 0o777, 0o640)
            self.assertEqual(target.read_bytes(), original_catalog)
            self.assertTrue(connection.exists())
            self.assertEqual(fake.containers, {'original': running_hub()})

    def test_default_cli_is_read_only_plan(self):
        result = subprocess.run([sys.executable, str(SOURCE), "lime"], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0)
        self.assertFalse(json.loads(result.stdout)["changes"])

    def test_verifier_uses_official_http_client_without_disclosing_credentials(self):
        try:
            import httpx2 as httpx
        except ImportError:
            import httpx
        import mcp.types
        registration = SETUP.descriptor(catalog())
        legacy = {'name': 'kachinco-lime.mcp.context', 'inputSchema': {'type': 'object', 'additionalProperties': False}}
        registration['existingTools'] = [legacy]
        include_legacy = True
        calls = []

        async def handle(request):
            self.assertEqual(request.headers["authorization"], "Bearer " + TOKEN)
            message = json.loads(request.content)
            method = message["method"]
            calls.append(method)
            if method == "notifications/initialized":
                return httpx.Response(202)
            if method == "initialize":
                value = {"protocolVersion": message["params"]["protocolVersion"], "capabilities": {"tools": {}},
                         "serverInfo": {"name": "test-hub", "version": "1"}}
            elif method == "tools/list":
                all_tools = [{"name": "cutwork-lime." + item["name"], "inputSchema": item["input_schema"]}
                             for item in registration["tools"]]
                cursor = message.get('params', {}).get('cursor')
                if cursor is None:
                    value = {'tools': all_tools[:10], 'nextCursor': 'part2'}
                elif cursor == 'part2':
                    value = {'tools': all_tools[10:], 'nextCursor': 'legacy'}
                elif cursor == 'legacy':
                    value = {'tools': [legacy] if include_legacy else []}
                else:
                    self.fail('Unexpected cursor')
            elif method == "tools/call":
                self.assertEqual(message["params"]["name"], "cutwork-lime.mcp.context")
                value = {"content": [{"type": "text", "text": json.dumps({"productId": "flamoris.cutwork",
                         "permission": "edit", "hasDocument": False, "runtimeId": "test-runtime"})}]}
            else:
                self.fail("Verifier sent an unexpected operation: " + method)
            return httpx.Response(200, json={"jsonrpc": "2.0", "id": message["id"], "result": value})

        real_client = httpx.AsyncClient
        def client(**kwargs):
            return real_client(**kwargs, transport=httpx.MockTransport(handle))
        configuration = types.ModuleType("flamoris_mcp_hub.config")
        configuration.env_file = lambda: pathlib.Path("/nonexistent-cutwork-test-env")
        with patch.dict(sys.modules, {"flamoris_mcp_hub": types.ModuleType("flamoris_mcp_hub"),
                                      "flamoris_mcp_hub.config": configuration}), \
             patch.dict(os.environ, {"FLAMORIS_MCP_HUB_CLIENT_TOKEN": TOKEN}), \
             patch.object(httpx, "AsyncClient", client), patch.object(sys, "stdin", io.StringIO(json.dumps(registration))):
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                exec(compile(SETUP.VERIFY, "hub-readonly-verifier", "exec"), {})
        self.assertNotIn(TOKEN, output.getvalue())
        self.assertTrue(json.loads(output.getvalue())["connected"])
        self.assertEqual(calls, ["initialize", "notifications/initialized", "tools/list", "tools/list", "tools/list", "tools/call"])
        include_legacy = False
        calls.clear()
        with patch.dict(sys.modules, {"flamoris_mcp_hub": types.ModuleType("flamoris_mcp_hub"),
                                      "flamoris_mcp_hub.config": configuration}), \
             patch.dict(os.environ, {"FLAMORIS_MCP_HUB_CLIENT_TOKEN": TOKEN}), \
             patch.object(httpx, "AsyncClient", client), patch.object(sys, "stdin", io.StringIO(json.dumps(registration))), \
             self.assertRaises(Exception):
            exec(compile(SETUP.VERIFY, 'reject-lost-existing-catalog', 'exec'), {})
        self.assertEqual(calls, ['initialize', 'notifications/initialized', 'tools/list', 'tools/list', 'tools/list'])


if __name__ == "__main__":
    unittest.main()

