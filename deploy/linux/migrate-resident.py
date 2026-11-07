#!/usr/bin/env python3
"""Prepare the native workspace for Updater; retain all rollback files."""
import argparse
import fcntl
import hashlib
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
    pointer = ROOT.with_name(ROOT.name + '-migration.json')
    native.no_links(pointer)
    if pointer.exists():
        saved = json.loads(pointer.read_text())
        if saved.get('phase') == 'prepared' and saved.get('source') == native.SOURCE and not ROOT.exists():
            legacy = pathlib.Path(saved['legacyRoot'])
            native.no_links(legacy)
            if (legacy.parent != ROOT.parent or not legacy.name.startswith(ROOT.name + '-native-retired-')
                    or not (legacy / 'state/connection.json').is_file()
                    or run(['systemctl', 'is-active', '--quiet', UNIT], check=False).returncode == 0):
                raise ValueError('Prepared native retirement changed; inspect before installation')
            print(json.dumps({'prepared': True, 'retained': True, 'legacyRoot': saved['legacyRoot']}))
            return
        raise ValueError('Preparation already recorded; inspect or run the restore stage after Updater installation')
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
    retired = ROOT.with_name(ROOT.name + '-native-retired-' + time.strftime('%Y%m%d-%H%M%S') + '-' + secrets.token_hex(3))
    moved = False
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
        ROOT.rename(retired)
        moved = True
        prepared = {'phase': 'prepared', 'source': native.SOURCE, 'legacyRoot': str(retired),
                    'backup': str(retired / backup.name), 'wasActive': was_active, 'wasEnabled': was_enabled}
        native.atomic(pointer, json.dumps(prepared).encode())
    except BaseException:
        try:
            if moved:
                retired.rename(ROOT)
            pointer.unlink(missing_ok=True)
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
    print(json.dumps({'prepared': True, 'endpoint': endpoint, 'backup': prepared['backup'], 'legacyRoot': str(retired),
                      'updaterApplication': 'flamoris-cutwork-cli'}))


def fingerprints(state):
    inventory(state)
    return {str(path.relative_to(state)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in state.rglob('*') if path.is_file()}


def restore(native):
    pointer = ROOT.with_name(ROOT.name + '-migration.json')
    native.no_links(pointer); native.no_links(ROOT)
    saved = json.loads(pointer.read_text())
    legacy = pathlib.Path(saved['legacyRoot'])
    if (saved.get('source') != native.SOURCE or saved.get('phase') not in ('prepared', 'restored')
            or legacy.parent != ROOT.parent or not legacy.name.startswith(ROOT.name + '-native-retired-')):
        raise ValueError('Unexpected prepared migration record')
    native.no_links(legacy)
    if run(['systemctl', 'is-active', '--quiet', UNIT], check=False).returncode == 0:
        raise ValueError('Native service must remain stopped during restoration')
    observed = json.loads(run(['docker', 'inspect', 'flamoris-cutwork-cli']).stdout)[0]
    if (observed['State']['Running'] or observed['Config']['User'] != '10001:10001'
            or not observed['HostConfig']['ReadonlyRootfs']
            or not any(m.get('Source') == str(ROOT / 'state') and m.get('Destination') == '/data/state'
                       and m.get('RW') is True for m in observed['Mounts'])):
        raise ValueError('Updater must have provisioned a stopped non-root container with the retained state mount')
    source, target = legacy / 'state', ROOT / 'state'
    native.no_links(target)
    if not target.is_dir() or target.is_symlink():
        raise ValueError('Updater state directory is missing')
    expected = fingerprints(source)
    if saved['phase'] == 'restored':
        if fingerprints(target) != expected:
            raise ValueError('Restored state changed; no overwrite')
        print(json.dumps({'restored': True, 'retained': True})); return
    if any(target.iterdir()):
        raise ValueError('Updater state must be empty before restoration; no overwrite')
    shutil.copytree(source, target, dirs_exist_ok=True)
    if fingerprints(target) != expected:
        raise ValueError('Restored state digest mismatch; retained original is unchanged')
    for path in [target, *target.rglob('*')]:
        os.chown(path, 10001, 10001)
        path.chmod(0o700 if path.is_dir() else 0o600)
    saved['phase'] = 'restored'
    native.atomic(ROOT / 'config/native-migration.json', json.dumps(saved).encode())
    native.atomic(pointer, json.dumps(saved).encode())
    print(json.dumps({'restored': True, 'files': len(expected), 'legacyRoot': str(legacy), 'hubVerification': 'pending'}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['prepare', 'restore'])
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if not args.apply:
        print(json.dumps({'changes': False, 'action': args.action, 'retained': str(ROOT / 'state')}))
        return
    if os.geteuid() != 0 or socket.gethostname().split('.')[0] != 'lime':
        raise ValueError('Run migration with sudo on LIME')
    spec = importlib.util.spec_from_file_location('cutwork_setup', pathlib.Path(__file__).with_name('setup-cutwork-lime.py'))
    native = importlib.util.module_from_spec(spec); spec.loader.exec_module(native)
    lock = ROOT.with_name(ROOT.name + '-migration.lock')
    native.no_links(lock); lock.parent.mkdir(parents=True, exist_ok=True)
    with lock.open('a') as held:
        os.fchmod(held.fileno(), 0o600)
        fcntl.flock(held, fcntl.LOCK_EX | fcntl.LOCK_NB)
        (prepare if args.action == 'prepare' else restore)(native)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        detail = str(error) if type(error) in (RuntimeError, ValueError) else type(error).__name__
        print('Migration stopped: ' + detail)
        raise SystemExit(1)
