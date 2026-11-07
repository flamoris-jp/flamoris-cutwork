#!/usr/bin/env python3
"""Actual image/process/storage/shutdown acceptance; Hub readiness is separate."""
import hashlib
import json
import os
import pathlib
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


def run(*arguments):
    return subprocess.run(list(arguments), check=True, capture_output=True, text=True, timeout=90).stdout


def main():
    image = sys.argv[1]
    name = 'cutwork-resident-test-' + secrets.token_hex(6)
    credential = secrets.token_urlsafe(48)
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0)); port = probe.getsockname()[1]
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with tempfile.TemporaryDirectory() as directory:
        state = pathlib.Path(directory)
        state.chmod(0o700)
        retained = state / 'retained.bin'; retained.write_bytes(b'retained authoring data')
        connection = state / 'connection.json'
        connection.write_text(json.dumps({'endpoint': 'wss://127.0.0.1:9/desktop/cutwork-lime', 'credential': credential}))
        connection.chmod(0o600)
        for path in [state, retained, connection]:
            os.chown(path, 10001, 10001)
        digest = hashlib.sha256(retained.read_bytes()).hexdigest()
        def health():
            with opener.open(f'http://127.0.0.1:{port}/healthz', timeout=3) as response:
                result = json.load(response)
            assert result['status'] == 'ready'
            assert result['hubReadiness'] == 'verify separately through Hub'
        def wait_ready():
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                try:
                    health(); return
                except (OSError, urllib.error.URLError, AssertionError):
                    time.sleep(.2)
            raise RuntimeError('Resident process did not become ready')
        try:
            run('docker', 'run', '-d', '--name', name, '--user', '10001:10001', '--read-only',
                '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--pids-limit', '128',
                '--memory', '2g', '--network', 'host', '--tmpfs', '/tmp:rw,noexec,nosuid,nodev,size=268435456,mode=1777',
                '--mount', f'type=bind,src={state},dst=/data/state', '--env', f'CUTWORK_PORT={port}', image)
            wait_ready()
            # Span a complete resident refresh interval with the Hub unavailable.
            deadline = time.monotonic() + 65
            while time.monotonic() < deadline:
                health(); time.sleep(1)
            inspected = json.loads(run('docker', 'inspect', name))[0]
            assert inspected['Config']['User'] == '10001:10001'
            assert inspected['HostConfig']['ReadonlyRootfs'] is True
            assert credential not in run('docker', 'logs', name)
            run('docker', 'stop', '--time', '30', name)
            assert json.loads(run('docker', 'inspect', name))[0]['State']['ExitCode'] == 0
            assert hashlib.sha256(retained.read_bytes()).hexdigest() == digest
            run('docker', 'start', name)
            wait_ready()
            assert json.loads(connection.read_text())['credential'] == credential
            assert hashlib.sha256(retained.read_bytes()).hexdigest() == digest
            print('Resident image ready, retained state preserved, outage survived, SIGTERM/restart passed. Hub connection requires live verification.')
        except BaseException:
            logs = subprocess.run(['docker', 'logs', name], capture_output=True, text=True)
            print((logs.stdout + logs.stderr).replace(credential, '[REDACTED]')[-6000:], file=sys.stderr)
            raise
        finally:
            subprocess.run(['docker', 'rm', '--force', name], capture_output=True)


if __name__ == '__main__':
    main()
