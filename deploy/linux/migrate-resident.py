#!/usr/bin/env python3
"""Prepare the native workspace for Updater; retain all rollback files."""
import argparse
import fcntl
import importlib.util
import json
import os
import pathlib
import secrets
import shutil
import socket
import stat
import subprocess
import time

ROOT = pathlib.Path('/srv/flamoris/apps/flamoris-cutwork-cli')
UNIT = 'flamoris-cutwork-cli.service'
SETTING = pathlib.Path('/etc/flamoris/cutwork-lime.env')
UNIT_PATH = pathlib.Path('/etc/systemd/system') / UNIT


def run(arguments, check=True):
    result = subprocess.run(arguments, capture_output=True, timeout=360)
    if check and result.returncode:
        raise RuntimeError('Command failed: ' + ' '.join(arguments[:2]))
    return result


def inventory(state):
    result = []
    for path in [state, *state.rglob('*')]:
        info = path.lstat()
        if path.is_symlink() or path.is_mount() or not (stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode)):
            raise ValueError('Linked, mounted or special workspace files require manual migration')
        result.append({'path': str(path.relative_to(state)), 'uid': info.st_uid,
                       'gid': info.st_gid, 'mode': stat.S_IMODE(info.st_mode)})
    return result


def restore_metadata(state, entries):
    for entry in entries:
        path = state / entry['path']
        os.chown(path, entry['uid'], entry['gid'])
        path.chmod(entry['mode'])


def prepare(native):
    native.no_links(ROOT); native.no_links(SETTING); native.no_links(UNIT_PATH)
    state, record = ROOT / 'state', ROOT / 'config/native-migration.json'
    native.no_links(state); native.no_links(record)
    if not state.is_dir() or not UNIT_PATH.is_file():
        raise ValueError('An existing native Cutwork service/workspace is required')
    if UNIT_PATH.read_bytes() != pathlib.Path(__file__).with_name(UNIT).read_bytes():
        raise ValueError('Native unit differs; inspect before migration')
    observed = run(['docker', 'inspect', 'flamoris-cutwork-cli'], check=False)
    if observed.returncode == 0:
        raise ValueError('Managed container already exists; inspect it before preparing native migration')
    if record.exists():
        connection = state / 'connection.json'
        if not connection.is_file() or connection.is_symlink() or connection.stat().st_mode & 0o077 or state.stat().st_uid != 10001:
            raise ValueError('Prepared migration state changed; inspect the retained backup')
        if run(['systemctl', 'is-active', '--quiet', UNIT], check=False).returncode == 0:
            raise ValueError('Native service is active after preparation; inspect before continuing')
        print(json.dumps({'prepared': True, 'retained': True}))
        return
    old = native.values(SETTING.read_text())
    endpoint = native.endpoint_from_existing(old['HUB_ENDPOINT'])
    credential = old[native.KEY]; native.append_credential('', credential)
    connection = state / 'connection.json'
    if connection.exists() or connection.is_symlink():
        raise ValueError('Existing connection.json requires inspection; no overwrite')
    was_active = run(['systemctl', 'is-active', '--quiet', UNIT], check=False).returncode == 0
    was_enabled = run(['systemctl', 'is-enabled', '--quiet', UNIT], check=False).returncode == 0
    backup = ROOT / ('native-backup-' + time.strftime('%Y%m%d-%H%M%S') + '-' + secrets.token_hex(3))
    backup.mkdir(mode=0o700)
    run(['systemctl', 'stop', UNIT])
    changed = False
    entries = []
    try:
        entries = inventory(state)
        shutil.copytree(state, backup / 'state')
        shutil.copy2(SETTING, backup / 'native.env')
        shutil.copy2(UNIT_PATH, backup / UNIT)
        native.atomic(backup / 'metadata.json', json.dumps(entries).encode())
        changed = True
        native.atomic(connection, json.dumps({'endpoint': endpoint, 'credential': credential}).encode(), uid=10001, gid=10001)
        for path in [state, *state.rglob('*')]:
            os.chown(path, 10001, 10001)
            path.chmod(0o700 if path.is_dir() else 0o600)
        run(['systemctl', 'disable', UNIT])
        native.atomic(record, json.dumps({'phase': 'prepared', 'source': native.SOURCE,
                      'backup': str(backup), 'wasActive': was_active, 'wasEnabled': was_enabled}).encode())
    except BaseException:
        try:
            if changed:
                connection.unlink(missing_ok=True)
                restore_metadata(state, entries)
            if was_enabled:
                run(['systemctl', 'enable', UNIT])
            if was_active:
                run(['systemctl', 'start', UNIT])
        except BaseException:
            raise RuntimeError('Preparation failed; native restoration needs inspection. Backup: ' + str(backup)) from None
        raise RuntimeError('Preparation failed; original native ownership/service restored. Backup retained: ' + str(backup)) from None
    print(json.dumps({'prepared': True, 'endpoint': endpoint, 'backup': str(backup),
                      'updaterApplication': 'flamoris-cutwork-cli'}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['prepare'])
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if not args.apply:
        print(json.dumps({'changes': False, 'action': args.action, 'retained': str(ROOT / 'state')}))
        return
    if os.geteuid() != 0 or socket.gethostname().split('.')[0] != 'lime':
        raise ValueError('Run migration with sudo on LIME')
    spec = importlib.util.spec_from_file_location('cutwork_setup', pathlib.Path(__file__).with_name('setup-cutwork-lime.py'))
    native = importlib.util.module_from_spec(spec); spec.loader.exec_module(native)
    lock = ROOT / 'config/.native-migration.lock'
    native.no_links(lock); lock.parent.mkdir(parents=True, exist_ok=True)
    with lock.open('a') as held:
        os.fchmod(held.fileno(), 0o600)
        fcntl.flock(held, fcntl.LOCK_EX | fcntl.LOCK_NB)
        prepare(native)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        detail = str(error) if type(error) in (RuntimeError, ValueError) else type(error).__name__
        print('Migration stopped: ' + detail)
        raise SystemExit(1)
