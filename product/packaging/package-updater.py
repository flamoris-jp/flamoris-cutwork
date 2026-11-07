#!/usr/bin/env python3
"""Prepare a repository-owned, digested Updater catalog. Never publishes it."""
import argparse
import hashlib
import json
import pathlib
import re
import shutil

ROOT = pathlib.Path(__file__).resolve().parents[2]


def package(image, image_id, revision, output):
    release = json.loads((ROOT / 'release/updater.json').read_text())
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', image_id) or not re.fullmatch(r'[0-9a-f]{40}', revision):
        raise ValueError('Immutable image ID and full source revision are required')
    if output.exists() or image.is_symlink() or not image.is_file():
        raise ValueError('Use a regular image archive and a new output directory')
    with image.open('rb') as stream:
        digest = 'sha256:' + hashlib.file_digest(stream, 'sha256').hexdigest()
    version = release['version']
    asset = 'linux-amd64--image.tar'
    url = f'https://github.com/flamoris-jp/flamoris-cutwork/releases/download/cli-v{version}/{asset}'
    settings = [
        dict(key='PORT', label='Local health port', default='8775', kind='port'),
        dict(key='HUB_ENDPOINT', label='Dedicated Cutwork WSS endpoint (empty uses retained connection.json)', default='', required=False),
        dict(key='HUB_TOKEN', label='Connector credential (empty uses retained connection.json)', default='', secret=True, required=False, kind='url_password'),
    ]
    runtime = ('CUTWORK_PORT=${PORT}\nCUTWORK_HUB_ENDPOINT=${HUB_ENDPOINT}\n'
               'CUTWORK_HUB_TOKEN=${HUB_TOKEN}\n')
    recipe = dict(application_id=release['application_id'], release=version, platform=release['platform'],
                  compatible_from=release['compatible_from'], dependencies={}, settings=settings,
                  downloads={'image.tar': dict(url=url, digest=digest)}, generated={'runtime.env': runtime},
                  application=dict(application_id=release['application_id'], release=version, kind='docker',
                      directories=[dict(path='${root}/config'),
                                   dict(path='${root}/state', uid=10001, gid=10001, mode=448)],
                      files=[dict(source=dict(path='${package}/runtime.env', digest='generated', private=True),
                                  destination='${root}/config/runtime.env')],
                      health_url='http://127.0.0.1:${PORT}/healthz',
                      image_archive=dict(path='${package}/image.tar', digest=digest, private=False), image_id=image_id,
                      container_name=release['application_id'], environment_file='${root}/config/runtime.env', network='host',
                      mounts=[dict(source='${root}/state', target='/data/state', read_only=False)]))
    output.mkdir(parents=True)
    shutil.copyfile(image, output / asset)
    (output / 'catalog.json').write_text(json.dumps(dict(catalog_version=1, recipes=[recipe], updater_releases=[]), indent=2) + '\n')
    (output / 'candidate.json').write_text(json.dumps(dict(application_id=release['application_id'], release=version,
        platform=release['platform'], source_revision=revision, image_id=image_id, archive_digest=digest), indent=2) + '\n')
    return recipe


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--image', type=pathlib.Path, required=True)
    parser.add_argument('--image-id', required=True)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    package(args.image, args.image_id, args.revision, args.output)

