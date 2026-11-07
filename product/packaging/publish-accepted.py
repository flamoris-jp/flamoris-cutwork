"""Publish accepted PR bytes only; never rebuild or replace an existing tag."""
import json
import os
import pathlib
import subprocess

def gh(*args):
    return subprocess.check_output(['gh',*args], text=True)

repo = os.environ['GITHUB_REPOSITORY']
revision = os.environ['GITHUB_SHA']
tree = subprocess.check_output(['git','rev-parse','HEAD^{tree}'],text=True).strip()
pulls = json.loads(gh('api',f'repos/{repo}/commits/{revision}/pulls'))
pull = next(p for p in pulls if p.get('merged_at') and p['head']['repo']['full_name'] == repo)
runs = json.loads(gh('api',f'repos/{repo}/actions/runs?head_sha={pull["head"]["sha"]}&event=pull_request'))['workflow_runs']
run = next(r for r in runs if r['conclusion']=='success' and r['path']=='.github/workflows/linux-cli.yml')
output = pathlib.Path('dist/accepted')
gh('run','download',str(run['id']),'--repo',repo,'--name','Cutwork-Updater-candidate','--dir',str(output))
subprocess.run(['sha256sum','--check','SHA256SUMS'],cwd=output,check=True)
candidate = json.loads((output/'candidate.json').read_text())
accepted = json.loads(gh('api',f'repos/{repo}/git/commits/{candidate["source_revision"]}'))
if accepted['tree']['sha'] != tree: raise RuntimeError('Accepted source tree differs from main')
release = json.loads(pathlib.Path('release/updater.json').read_text())
if candidate['release'] != release['version'] or candidate['application_id'] != release['application_id']:
    raise RuntimeError('Accepted application/version differs')
tag = 'cli-v'+release['version']
if subprocess.run(['gh','api',f'repos/{repo}/git/ref/tags/{tag}'],capture_output=True).returncode == 0:
    raise RuntimeError('Release tag already exists; no replacement')
gh('release','create',tag,*map(str,output.iterdir()),'--repo',repo,'--target',revision,
   '--title','Cutwork Linux CLI '+release['version'],'--notes-file','release/cli-notes.md','--latest=false')

