#!/usr/bin/env python3
"""Retire only the verified failed 0.1.0 stage; preserve all audit records."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import socket
import stat
import subprocess
import sys

APP = 'flamoris-cutwork-cli'
ROOT = Path('/srv/flamoris/apps') / APP
RECIPE = 'sha256:c0a550aadacc20c7cade39d31dab0a53de5cd5c7ce5ceee631eab4116b3a53ff'
IMAGE = 'sha256:e1c3518d51a36b90ec734d8ffd1259c8687aa1d2e1776d3e0b785fbfae74ddfd'
RUNTIME = Path('/var/lib/flamoris-updater/releases/ca1e54d96a84e2aaa2b83ac9ed9aa746a7a1266992510b404261b16d1d679277')
UNITS = ('flamoris-updater-supervisor.service', 'flamoris-updater.service', 'flamoris-updater-manager.service')


def require(value, message):
    if not value: raise ValueError(message)


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, timeout=180).stdout


def trusted(path):
    require(path.is_absolute() and '..' not in path.parts, 'Invalid control path')
    for item in reversed((path, *path.parents)):
        info = item.lstat()
        require(not stat.S_ISLNK(info.st_mode) and info.st_uid == 0 and not info.st_mode & 0o022,
                'Protected administrator-owned control paths are required')
    return path


def load_runtime():
    trusted(RUNTIME / 'site-packages')
    unit = trusted(Path('/etc/systemd/system/flamoris-updater-manager.service'))
    starts = [line.split('=', 1)[1] for line in unit.read_text().splitlines() if line.startswith('ExecStart=')]
    require(len(starts) == 1, 'Unexpected Updater unit')
    args = shlex.split(starts[0])
    require(args[0] == str(RUNTIME / 'bin/flamoris-updater-service') and args.count('--config') == 1,
            'Updater runtime changed; inspect before recovery')
    config = trusted(Path(args[args.index('--config') + 1]))
    sys.path.insert(0, str(RUNTIME / 'site-packages'))
    from flamoris_updater_adapters.config import load
    from flamoris_updater_adapters.setup import BootstrapConfig
    from flamoris_updater_adapters.managed import Manager
    cfg = load(BootstrapConfig, str(config), root_only=True)
    require(Path(cfg.root) / APP == ROOT, 'Unexpected application namespace')
    manager = Manager(cfg.helper_state_directory, cfg.root, self_configuration=cfg)
    current = manager.journal.get('self_installation', 'current')
    require(current and (current['release'], current['runtime'], current['phase']) ==
            ('1.0.15', str(RUNTIME), 'succeeded'), 'Installed Updater changed')
    return manager


def inspect_failure(manager, identity, root, expected_recipe, expected_image):
    from flamoris_update_core.wire import decode, digest, dumps
    from flamoris_updater_adapters.managed import Recipe
    from flamoris_updater_adapters.journal import Journal
    job = manager.journal.get('managed_job', identity)
    require(job and (job.get('application_id'), job.get('release'), job.get('action'), job.get('phase'),
                    job.get('step'), job.get('error')) ==
            (APP, '0.1.0', 'install', 'recovery_required', 'install', 'artifact_mismatch'),
            'Only the unchanged failed 0.1.0 image staging can be retired')
    require(not manager.journal.get('managed_app', APP), 'Application already has a managed installation')
    with manager.journal.connection() as db:
        other = db.execute("SELECT id FROM records WHERE kind='managed_job' AND id!=? AND json_extract(payload,'$.phase') IN ('accepted','running','intent','recovery_required')", (identity,)).fetchone()
    require(not other, 'Another active operation needs attention')
    raw = manager.journal.get('managed_recipe', identity)
    require(raw is not None, 'Original recipe is missing')
    recipe = decode(Recipe, dumps({key: value for key, value in raw.items() if key != '_revision'}))
    require(digest(dumps(recipe)) == job['recipe'] == expected_recipe, 'Original recipe binding changed')
    cfg = manager._profile(recipe, identity, reuse=True)
    app = cfg.applications[0]
    require(app.application_id == APP and app.image_id == expected_image and app.database is None,
            'Unexpected failed application profile')
    journal = Journal(Path(cfg.state_directory))
    initial = journal.get('initial_install', APP)
    archive = journal.get('retired_initial_install', identity)
    if initial is None:
        require(archive and archive['job_id'] == identity, 'Missing active or archived failure record')
        initial = archive['initial']
    require(initial['phase'] == 'recovery_required' and initial['step'] == 'stage' and
            initial['application_id'] == APP and initial['profile_digest'] == digest(dumps(cfg)),
            'Failure progressed beyond the known image staging boundary')
    names = command('/usr/bin/docker', '--host', 'unix:///var/run/docker.sock', 'container', 'ls', '--all', '--format', '{{.Names}}')
    require(APP not in names.decode().splitlines(), 'A Cutwork container exists; no quarantine')
    quarantine = root.with_name(root.name + '-rejected-' + identity[4:])
    source = root if root.exists() else quarantine
    require(source.is_dir() and not source.is_symlink() and not source.is_mount(), 'Partial installation is missing')
    paths = {str(path.relative_to(source)): path for path in source.rglob('*')}
    require(set(paths) == {'config', 'config/runtime.env', 'state'}, 'Partial root contains unexpected files')
    require(all(not path.is_symlink() and not path.is_mount() for path in paths.values()),
            'Partial root contains links or mounts')
    require(not any((source / 'state').iterdir()), 'Partial state is populated; no quarantine')
    require(len(app.files) == 1 and app.files[0].destination == str(root / 'config/runtime.env'),
            'Unexpected configuration file set')
    app.files[0].source.verify()
    require(hashlib.sha256((source / 'config/runtime.env').read_bytes()).digest() ==
            hashlib.sha256(Path(app.files[0].source.path).read_bytes()).digest(),
            'Partial configuration changed')
    return job, journal, initial, source, quarantine


def retire(manager, identity, *, root=ROOT, expected_recipe=RECIPE, expected_image=IMAGE, apply=False):
    from flamoris_updater_adapters.journal import exclusive
    from flamoris_updater_adapters.diagnostics import recording, event
    with exclusive(manager.state / 'manager.lock'):
        job, journal, initial, source, quarantine = inspect_failure(manager, identity, root, expected_recipe, expected_image)
        result = {'application': APP, 'job': identity, 'quarantined': False, 'retainedPartialRoot': str(quarantine)}
        if not apply: return result
        with recording(manager.journal, identity, manager.secrets(identity)):
            event('recovery.quarantine', 'intent', {'path': str(quarantine)})
            if source == root:
                require(not quarantine.exists() and not quarantine.is_symlink(), 'Quarantine destination exists')
                root.rename(quarantine)
                quarantine.chmod(0o700)
            with journal.transaction() as db:
                archived = journal.get('retired_initial_install', identity, db)
                if archived is None:
                    journal.put('retired_initial_install', identity, {'job_id': identity, 'initial': initial,
                                'partial_root': str(quarantine), 'reason': 'verified_failed_image_stage'}, db)
                # Retire only this active pointer after its complete record has
                # been archived in the same transaction; all history is retained.
                db.execute("DELETE FROM records WHERE kind='initial_install' AND id=?", (APP,))
                journal.event(db, 'initial_install_quarantined', identity)
            journal.flush_export()
            job.update(phase='failed', recovery={'kind': 'verified_failed_image_stage',
                       'partial_root': str(quarantine), 'initial_record': identity})
            manager.persist('managed_job', identity, job)
            event('recovery.quarantine', 'verified', {'path': str(quarantine)})
        return {**result, 'quarantined': True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--job', required=True)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    require(os.geteuid() == 0 and socket.gethostname().split('.')[0] == 'lime', 'Run with sudo on LIME')
    manager = load_runtime()
    stopped = False
    try:
        if args.apply:
            command('systemctl', 'stop', *UNITS); stopped = True
            for unit in UNITS:
                require(command('systemctl', 'show', unit, '--property=ActiveState', '--value').strip() == b'inactive',
                        'Updater services must be inactive during retirement')
        print(json.dumps(retire(manager, args.job, apply=args.apply)))
    finally:
        if stopped: command('systemctl', 'start', *reversed(UNITS))


if __name__ == '__main__':
    try: main()
    except Exception as error:
        print('Quarantine stopped: ' + (str(error) if isinstance(error, ValueError) else type(error).__name__))
        raise SystemExit(1)
