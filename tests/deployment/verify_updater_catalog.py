#!/usr/bin/env python3
"""Validate through the actual Updater; real installation is disposable-CI only."""
import argparse
import hashlib
import copy
import importlib.util
import json
import os
import pathlib
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.request

from flamoris_updater_adapters.managed import Catalog, Manager, Start


def verify(catalog_path, install=False):
    catalog = Catalog.model_validate_json(catalog_path.read_bytes())
    if len(catalog.recipes) != 1 or catalog.recipes[0].application_id != 'flamoris-cutwork-cli':
        raise ValueError('Expected one CLI release candidate')
    if install and (os.geteuid() != 0 or os.environ.get('GITHUB_ACTIONS') != 'true'):
        raise ValueError('Actual installation requires a disposable root CI runner')
    recipe = catalog.recipes[0]
    with tempfile.TemporaryDirectory(prefix='cutwork-updater-') as directory:
        root = pathlib.Path(directory)
        media = root / 'media'
        media.mkdir(mode=0o755)
        manager = Manager(root / 'state', root / 'apps')
        manager.catalog = lambda refresh=False: catalog
        manager.accept_catalog(catalog)
        sources = {}

        def download(url, limit, destination, expected):
            name = url.rsplit('/', 1)[1]
            source = sources.get(name, catalog_path.parent / name)
            if source.stat().st_size > limit:
                raise ValueError('Archive exceeds Updater limit')
            with source.open('rb') as stream:
                actual = 'sha256:' + hashlib.file_digest(stream, 'sha256').hexdigest()
            if actual != expected:
                raise ValueError('Archive digest mismatch')
            shutil.copyfile(source, destination)

        manager._download = download
        port = 8772
        if install:
            with socket.socket() as available:
                available.bind(('127.0.0.1', 0))
                port = available.getsockname()[1]
        values = dict(PORT=str(port), HUB_ENDPOINT='wss://127.0.0.1:9/desktop/cutwork-lime',
                      HUB_TOKEN=secrets.token_urlsafe(32))
        if install:
            # Reproduce the rejected 0.1.0 metadata, then exercise the guarded
            # audited retirement before a normal corrected installation.
            bad_tag = 'cutwork-invalid-updater-fixture'
            subprocess.run(['docker', 'build', '-t', bad_tag, '-'], check=True, capture_output=True, text=True,
                           input='FROM cutwork-cli-candidate\nLABEL org.opencontainers.image.title="fixture-invalid" org.opencontainers.image.version="0.1.0"\n')
            bad_id = json.loads(subprocess.check_output(['docker', 'image', 'inspect', bad_tag]))[0]['Id']
            bad_archive = root / 'bad-image.tar'
            subprocess.run(['docker', 'image', 'save', '-o', str(bad_archive), bad_tag], check=True)
            with bad_archive.open('rb') as stream:
                bad_digest = 'sha256:' + hashlib.file_digest(stream, 'sha256').hexdigest()
            sources['bad-image.tar'] = bad_archive
            raw = copy.deepcopy(json.loads(catalog_path.read_bytes()))
            bad = raw['recipes'][0]; bad['release'] = '0.1.0'
            bad_url = bad['downloads']['image.tar']['url'].rsplit('/', 1)[0] + '/bad-image.tar'
            bad['downloads']['image.tar'].update(url=bad_url, digest=bad_digest)
            bad['application']['release'] = '0.1.0'
            bad['application']['image_id'] = bad_id
            bad['application']['image_archive']['digest'] = bad_digest
            bad_catalog = Catalog.model_validate(raw)
            manager.catalog = lambda refresh=False: bad_catalog
            manager.accept_catalog(bad_catalog)
            rejected = manager.start('install', Start(application_id=recipe.application_id, release='0.1.0',
                                     request_key='invalid-image-label-fixture', settings=values))
            manager.run_job(rejected['job_id'])
            failed = manager.journal.get('managed_job', rejected['job_id'])
            assert failed['phase'] == 'recovery_required' and failed['error'] == 'artifact_mismatch', failed
            spec = importlib.util.spec_from_file_location('quarantine',
                    pathlib.Path(__file__).resolve().parents[2] / 'deploy/linux/quarantine-failed-install.py')
            quarantine = importlib.util.module_from_spec(spec); spec.loader.exec_module(quarantine)
            app_root = root / 'apps' / recipe.application_id
            marker = app_root / 'state/not-empty'
            marker.write_bytes(b'preserve user data')
            try:
                quarantine.retire(manager, rejected['job_id'], root=app_root,
                                  expected_recipe=rejected['recipe'], expected_image=bad_id, apply=True)
            except ValueError:
                assert marker.read_bytes() == b'preserve user data'
            else:
                raise AssertionError('Populated partial state was incorrectly retired')
            marker.unlink()
            retired = quarantine.retire(manager, rejected['job_id'], root=app_root,
                                        expected_recipe=rejected['recipe'], expected_image=bad_id, apply=True)
            assert retired['quarantined'] and not app_root.exists()
            assert manager.journal.get('managed_job', rejected['job_id'])['phase'] == 'failed'
            assert pathlib.Path(retired['retainedPartialRoot']).is_dir()
            manager.catalog = lambda refresh=False: catalog
            manager.accept_catalog(catalog)
        job = manager.start('install', Start(application_id=recipe.application_id, release=recipe.release,
                            request_key='cli-repository-acceptance', settings=values))
        try:
            if not install:
                profile = manager._profile(recipe, job['job_id'])
                assert profile.applications[0].application_id == recipe.application_id
            else:
                manager.run_job(job['job_id'])
                recorded = manager.journal.get('managed_job', job['job_id'])
                assert recorded['phase'] == 'awaiting_setup', recorded
                retained = root / 'apps' / recipe.application_id / 'state' / 'retained-marker'
                retained.write_bytes(b'prior native state')
                manager.start_setup(recipe.application_id)
                completed = manager.complete(recipe.application_id)
                assert completed['phase'] == 'succeeded', completed
                container = recipe.application['container_name']
                state = root / 'apps' / recipe.application_id / 'state'
                project = state / 'service.lock'
                assert retained.read_bytes() == b'prior native state'
                assert project.is_file()
                before = project.read_bytes()
                # The bad Hub remains unavailable: native process must stay resident.
                time.sleep(8)
                subprocess.run(['docker','exec',container,'/app/cutwork-cli','mcp-catalog'],check=True,timeout=30,stdout=subprocess.DEVNULL)
                observed = json.loads(subprocess.check_output(['docker','inspect',container]))[0]
                assert observed['RestartCount'] == 0 and observed['State']['Running']
                subprocess.run(['docker','restart',container],check=True,timeout=60,stdout=subprocess.DEVNULL)
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    try:
                        with urllib.request.urlopen(f'http://127.0.0.1:{port}/healthz',timeout=2) as response:
                            if json.load(response)['status'] == 'ready': break
                    except OSError: pass
                    time.sleep(.2)
                else: raise RuntimeError('Resident host did not restart')
                assert project.read_bytes() == before
        finally:
            if install:
                subprocess.run(['docker','logs','--tail','80',recipe.application['container_name']],check=False,timeout=15)
                subprocess.run(['docker', 'rm', '--force', recipe.application['container_name']], check=False,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60)
    print(json.dumps(dict(catalog_validated=True, installation_performed=install, hub_connection_verified=False)))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('catalog', type=pathlib.Path)
    parser.add_argument('--install', action='store_true')
    options = parser.parse_args()
    verify(options.catalog, options.install)


