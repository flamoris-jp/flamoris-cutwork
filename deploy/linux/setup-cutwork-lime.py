#!/usr/bin/env python3
"""Install the reviewed LIME bundle, then register its separate DECO PON route.

No changes without --apply. Run from the normal operator account via sudo.
Credentials remain in private files or subprocess stdin, never terminal output.
"""
import argparse
import copy
import fcntl
import hashlib
import http.client
import json
import os
import pathlib
import pwd
import re
import secrets
import shlex
import shutil
import socket
import subprocess
import tarfile
import tempfile
import time
from urllib.parse import quote, urlsplit, urlunsplit

REPOSITORY = "flamoris-jp/flamoris-cutwork"
RUN_ID = "42355265811"
SOURCE = "36b443c7e793896583a842876ef8b1073fee28e6"
ARCHIVE_SHA256 = "0b6b38f385cfd97343e73f058f52de07ff2caee9ee9c9f66fd79db7e0019a385"
ROOT = pathlib.Path("/srv/flamoris/apps/flamoris-cutwork-cli")
HUB_ROOT = pathlib.Path("/srv/flamoris/apps/flamoris-mcp-hub")
KEY = "CUTWORK_LIME_CONNECTOR_TOKEN"
UNIT = "flamoris-cutwork-cli.service"
READS = {
    "mcp.context", "context", "layers", "image", "part_preview", "part_candidates",
    "part_polygon_preview", "work_log_read", "project_context", "project_save_status",
    "project_checkpoints_list", "repair_inspect", "clone_candidates", "workspace_status", "artifact_read",
}


def run(arguments, *, data=None, check=True, timeout=60):
    result = subprocess.run(arguments, input=data, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=timeout)
    if check and result.returncode:
        raise RuntimeError("Command failed: " + str(arguments[0]))
    return result


def values(text):
    result = {}
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        key, separator, value = stripped.partition("=")
        if not separator or not re.fullmatch(r"[A-Z][A-Z0-9_]*", key) or key in result:
            raise ValueError("Invalid/duplicate environment setting")
        tokens = shlex.split(value, comments=True)
        result[key] = tokens[0] if len(tokens) == 1 else ("" if not tokens else value)
    return result


def append_credential(text, token):
    if not re.fullmatch(r"[A-Za-z0-9_-]{40,128}", token):
        raise ValueError("Invalid connector credential")
    old = values(text)
    if KEY in old:
        if old[KEY] != token:
            raise ValueError("An existing Cutwork credential differs; no automatic rotation")
        return text
    return text.rstrip("\n") + "\n" + KEY + "=" + token + "\n"


def endpoint_from_existing(endpoint):
    parsed = urlsplit(endpoint)
    if (parsed.scheme != "wss" or not parsed.hostname or parsed.username or parsed.password
            or parsed.query or parsed.fragment or "/desktop/" not in parsed.path):
        raise ValueError("Supply a dedicated WSS --hub-endpoint; existing endpoint is not suitable")
    prefix, _, identifier = parsed.path.rpartition("/desktop/")
    if not identifier or "/" in identifier:
        raise ValueError("Invalid existing desktop route")
    return urlunsplit((parsed.scheme, parsed.netloc, prefix + "/desktop/cutwork-lime", "", ""))


def descriptor(raw):
    raw = json.loads(json.dumps(raw))
    if (raw.get("id") != "cutwork-lime" or raw.get("namespace") != "cutwork-lime"
            or raw.get("product_id") != "flamoris.cutwork" or raw.get("transport") != "reverse-websocket"
            or raw.get("connector_token_env") != KEY or len(raw.get("tools", [])) != 29):
        raise ValueError("Unexpected reviewed Cutwork descriptor")
    names = [tool["name"] for tool in raw["tools"]]
    if len(set(names)) != 29 or "document_import" not in names or "mcp.context" not in names:
        raise ValueError("Invalid Cutwork tool set")
    for tool in raw["tools"]:
        query = tool["name"] in READS
        tool["annotations"] = {"readOnlyHint": query, "destructiveHint": not query,
                               "openWorldHint": False}
    return raw


def no_links(path):
    for parent in [path, *path.parents]:
        if parent.is_symlink():
            raise ValueError("Deployment path must not contain symbolic links")


def atomic(path, content, mode=0o600, uid=0, gid=0):
    no_links(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name("." + path.name + "." + secrets.token_hex(8))
    try:
        with temporary.open("xb") as output:
            os.fchmod(output.fileno(), mode)
            if os.geteuid() == 0:
                os.fchown(output.fileno(), uid, gid)
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def extract_verified(archive, destination):
    with archive.open("rb") as source:
        if hashlib.file_digest(source, "sha256").hexdigest() != ARCHIVE_SHA256:
            raise ValueError("Bundle digest mismatch; nothing installed")
    with tarfile.open(archive) as bundle:
        members = bundle.getmembers()
        if len(members) > 1000 or sum(member.size for member in members) > 512 * 1024 * 1024:
            raise ValueError("Bundle size limit")
        for member in members:
            name = pathlib.PurePosixPath(member.name)
            if name.is_absolute() or ".." in name.parts or not (member.isdir() or member.isfile()):
                raise ValueError("Unsafe bundle member")
        bundle.extractall(destination, filter="data")
    # Python's data filter deliberately discards directory modes. The staging
    # directory starts 0700; the dedicated service account must traverse it.
    destination.chmod(0o755)
    for directory in destination.rglob("*"):
        if directory.is_dir():
            directory.chmod(0o755)
    if not (destination / "cutwork-cli").is_file():
        raise ValueError("Executable missing")


def operator():
    user = os.environ.get("SUDO_USER", "")
    if not user or user == "root":
        raise ValueError("Run via sudo from your normal authenticated GitHub operator account")
    return pwd.getpwnam(user)


def as_operator(user, arguments, **kwargs):
    return run(["runuser", "-u", user.pw_name, "--", *arguments], **kwargs)


def lime(args, user):
    if socket.gethostname().split(".")[0].lower() != "lime":
        raise ValueError("The lime phase must run on LIME")
    no_links(ROOT)
    ROOT.mkdir(parents=True, exist_ok=True)
    ROOT.chmod(0o755)
    docker = run(["docker", "inspect", "flamoris-cutwork-cli"], check=False)
    if docker.returncode == 0:
        raise ValueError("A Docker Cutwork installation exists; do not install a second host")
    setting = pathlib.Path("/etc/flamoris/cutwork-lime.env")
    old = values(setting.read_text()) if setting.exists() else {}
    if args.hub_endpoint:
        endpoint = endpoint_from_existing(args.hub_endpoint)
    elif "HUB_ENDPOINT" in old:
        endpoint = endpoint_from_existing(old["HUB_ENDPOINT"])
    else:
        previous = pathlib.Path("/srv/flamoris/apps/flamoris-kachinco-cli/config/runtime.env")
        endpoint = endpoint_from_existing(values(previous.read_text()).get("KACHINCO_HUB_ENDPOINT", ""))
    token = old.get(KEY) or secrets.token_urlsafe(48)
    append_credential("", token)
    cache = ROOT / "packages"
    cache.mkdir(parents=True, exist_ok=True)
    archive = pathlib.Path(args.archive).resolve() if args.archive else cache / (SOURCE + ".tar.gz")
    if not archive.exists():
        as_operator(user, ["gh", "auth", "status"])
        temporary = pathlib.Path(tempfile.mkdtemp(prefix="cutwork-download-"))
        os.chown(temporary, user.pw_uid, user.pw_gid)
        try:
            as_operator(user, ["gh", "run", "download", RUN_ID, "--repo", REPOSITORY,
                               "--name", "Cutwork-LIME-linux-x64", "--dir", str(temporary)], timeout=180)
            shutil.copyfile(temporary / "cutwork-lime-linux-x64.tar.gz", archive)
            archive.chmod(0o600)
        finally:
            shutil.rmtree(temporary)
    release = ROOT / "releases" / SOURCE
    if not release.exists():
        staged = pathlib.Path(tempfile.mkdtemp(prefix=".cutwork-", dir=cache))
        try:
            extract_verified(archive, staged)
            release.parent.mkdir(parents=True, exist_ok=True)
            release.parent.chmod(0o755)
            staged.rename(release)
        finally:
            if staged.exists():
                shutil.rmtree(staged)
    else:
        with archive.open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != ARCHIVE_SHA256:
                raise ValueError("Cached bundle digest mismatch")
    registration = descriptor(json.loads((release / "cutwork-lime.json").read_text()))
    observed = json.loads(run([str(release / "cutwork-cli"), "mcp-catalog"]).stdout)
    if descriptor(observed) != registration:
        raise ValueError("Installed executable does not match reviewed catalog")
    unit = pathlib.Path("/etc/systemd/system") / UNIT
    unit_bytes = (release / UNIT).read_bytes()
    if unit.exists() and unit.read_bytes() != unit_bytes:
        raise ValueError("Existing service unit differs; no automatic replacement")
    binary = ROOT / "bin"
    if binary.exists() and not binary.is_symlink():
        raise ValueError("Existing non-managed bin directory; no automatic replacement")
    try:
        account = pwd.getpwnam("flamoris-cutwork")
    except KeyError:
        run(["useradd", "--system", "--user-group", "--home-dir", str(ROOT / "state"),
             "--shell", "/usr/sbin/nologin", "flamoris-cutwork"])
        account = pwd.getpwnam("flamoris-cutwork")
    state = ROOT / "state"
    no_links(state)
    state.mkdir(exist_ok=True)
    os.chown(state, account.pw_uid, account.pw_gid)
    state.chmod(0o700)
    run(["runuser", "-u", account.pw_name, "--", str(release / "cutwork-cli"), "--help"])
    link = ROOT / (".bin-" + secrets.token_hex(8))
    link.symlink_to(release)
    os.replace(link, binary)
    atomic(setting, ("HUB_ENDPOINT=" + endpoint + "\n" + KEY + "=" + token + "\n").encode())
    atomic(unit, unit_bytes, 0o644)
    connection = pathlib.Path(user.pw_dir) / "cutwork-lime-connect.json"
    atomic(connection, json.dumps({"version": 1, "source": SOURCE, "archiveSha256": ARCHIVE_SHA256,
           "endpoint": endpoint, "credential": token, "descriptor": registration}, indent=2).encode(),
           uid=user.pw_uid, gid=user.pw_gid)
    run(["systemctl", "daemon-reload"])
    run(["systemctl", "enable", "--now", UNIT])
    run(["systemctl", "is-active", "--quiet", UNIT])
    copied = as_operator(user, ["scp", "-q", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                                  str(connection), user.pw_name + "@decopon:cutwork-lime-connect.json"],
                         check=False, timeout=25).returncode == 0
    print(json.dumps({"installed": True, "active": True, "source": SOURCE,
                      "hubSetupPending": True, "privateBundleCopiedToDecopon": copied}))
    if not copied:
        print("Copy the private file using: scp ~/cutwork-lime-connect.json " + user.pw_name + "@decopon:cutwork-lime-connect.json")


CONTAINER_ENV = r'''
import json,os,sys
from dotenv import dotenv_values
from flamoris_mcp_hub.config import env_file
r=json.load(sys.stdin);key=r['key'];token=r['credential']
p=env_file().absolute()
if p.is_symlink():raise SystemExit('Credential env path is a link')
existing=dict(dotenv_values(p)) if p.exists() else {}
if existing.get(key) not in (None,'',token) or os.environ.get(key) not in (None,'',token):
 raise SystemExit('Credential conflict')
print(json.dumps({'validated':True}))
'''

VALIDATE = r'''
import json,sys
from flamoris_mcp_hub.config import UpstreamConfig,load_upstreams,config_dir
d=UpstreamConfig.model_validate(json.load(sys.stdin))
previous=load_upstreams(config_dir())
for old in previous:
 if (old.id==d.id or old.namespace==d.namespace) and old.model_dump()!=d.model_dump():
  raise SystemExit('Existing upstream conflicts; no overwrite')
print(json.dumps({'validated':True,'tools':len(d.tools),'existingTools':[
 {'name':old.namespace+'.'+tool.name,'inputSchema':tool.input_schema}
 for old in previous for tool in old.tools]}))
'''

VERIFY = r'''
import asyncio,inspect,json,os,sys
try:
 import httpx2 as httpx
except ImportError:
 import httpx
from dotenv import load_dotenv
from flamoris_mcp_hub.config import env_file
from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
load_dotenv(env_file(),override=False)
expected=json.load(sys.stdin)
async def main():
 port=os.environ.get('FLAMORIS_MCP_HUB_PORT','8769')
 headers={'Authorization':'Bearer '+os.environ['FLAMORIS_MCP_HUB_CLIENT_TOKEN']}
 async with httpx.AsyncClient(headers=headers,timeout=20,trust_env=False) as http:
  url='http://127.0.0.1:'+port+'/mcp'
  transport=streamable_http_client(url,http_client=http) if 'http_client' in inspect.signature(streamable_http_client).parameters else streamable_http_client(url,headers=headers)
  async with transport as (read,write,*_):
   async with ClientSession(read,write) as session:
    await session.initialize()
    tools=(await session.list_tools()).model_dump(by_alias=True)['tools']
    actual={t['name']:t for t in tools}
    for tool in expected.get('existingTools',[]):
     if tool['name'] not in actual or actual[tool['name']]['inputSchema']!=tool['inputSchema']:raise RuntimeError('Existing catalog mismatch')
    for tool in expected['tools']:
     public=expected['namespace']+'.'+tool['name']
     if public not in actual or actual[public]['inputSchema']!=tool['input_schema']:raise RuntimeError('Catalog mismatch')
    result=(await session.call_tool(expected['namespace']+'.mcp.context',{})).model_dump(by_alias=True)
    if result.get('isError'):raise RuntimeError('Upstream not ready')
    value=json.loads(next(x['text'] for x in result['content'] if x['type']=='text'))
    if value.get('productId')!='flamoris.cutwork' or value.get('permission')!='edit':raise RuntimeError('Unexpected host')
    print(json.dumps({'connected':True,'toolCount':len(expected['tools']),'permission':value['permission'],
                     'hasDocument':value['hasDocument'],'runtimeId':value['runtimeId']}))
asyncio.run(main())
'''


class DockerSocket(http.client.HTTPConnection):
    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect('/var/run/docker.sock')


def docker_api(method, path, body=None):
    """Send credentials over the local daemon socket, never command arguments."""
    client = DockerSocket('localhost', timeout=60)
    try:
        payload = None if body is None else json.dumps(body).encode()
        client.request(method, path, body=payload, headers={'Content-Type': 'application/json'})
        response = client.getresponse()
        data = response.read(4 * 1024 * 1024)
        if response.status >= 300:
            # Docker errors can contain the request; do not echo response bodies.
            raise RuntimeError('Docker API returned HTTP ' + str(response.status))
        return json.loads(data) if data else None
    finally:
        client.close()


def replacement_spec(observed, token):
    """Retain the exact image, hardening, mounts, network and existing settings."""
    config = copy.deepcopy(observed['Config'])
    host = copy.deepcopy(observed['HostConfig'])
    if (not observed['State']['Running'] or not host.get('ReadonlyRootfs')
            or host.get('NetworkMode') != 'host' or host.get('AutoRemove')
            or host.get('ContainerIDFile') or host.get('Links') or host.get('VolumesFrom')
            or any(mount['Type'] not in ('bind', 'tmpfs') for mount in observed.get('Mounts', []))):
        raise ValueError('Hub replacement requires the running read-only host-network container with bind/tmpfs mounts')
    current = {}
    for entry in config.get('Env') or []:
        key, separator, value = entry.partition('=')
        if not separator or key in current:
            raise ValueError('Ambiguous container environment; no replacement')
        current[key] = value
    if current.get(KEY) not in (None, '', token):
        raise ValueError('Container has a different Cutwork credential; no takeover')
    append_credential('', token)
    current[KEY] = token
    config['Env'] = [key + '=' + value for key, value in current.items()]
    config['Image'] = observed['Image']  # Immutable local image ID, no pull/tag change.
    config['HostConfig'] = host
    return config


class HubSwap:
    """Keep the stopped original available until the new route is verified."""
    def __init__(self, observed, token, api=None):
        self.observed = observed
        self.api = api or docker_api
        self.spec = replacement_spec(observed, token)
        self.name = observed['Name'].lstrip('/')
        suffix = secrets.token_hex(6)
        self.candidate_name = self.name + '-cutwork-candidate-' + suffix
        self.previous_name = self.name + '-cutwork-previous-' + suffix
        self.candidate = None

    def prepare(self):
        self.candidate = self.api('POST', '/containers/create?name=' + quote(self.candidate_name), self.spec)['Id']
        found = self.api('GET', '/containers/' + self.candidate + '/json')
        # Refuse a partial/normalized clone before stopping the current Hub.
        if found['Image'] != self.observed['Image'] or found['HostConfig'] != self.spec['HostConfig']:
            raise RuntimeError('Replacement container settings differ; original Hub remains running')
        for key, value in self.spec.items():
            if key not in ('HostConfig', 'Image') and found['Config'].get(key) != value:
                raise RuntimeError('Replacement Config.' + key + ' differs; original Hub remains running')
        wanted_mounts = [{key: mount.get(key) for key in ('Type', 'Source', 'Destination', 'RW', 'Propagation')}
                         for mount in self.observed.get('Mounts', [])]
        found_mounts = [{key: mount.get(key) for key in ('Type', 'Source', 'Destination', 'RW', 'Propagation')}
                        for mount in found.get('Mounts', [])]
        if wanted_mounts != found_mounts:
            raise RuntimeError('Replacement mounts differ; original Hub remains running')

    def activate(self):
        original = '/containers/' + self.observed['Id']
        current = self.api('GET', original + '/json')
        if (current['Name'] != self.observed['Name'] or not current['State']['Running']
                or current['Config'] != self.observed['Config'] or current['HostConfig'] != self.observed['HostConfig']):
            raise RuntimeError('Hub changed during setup; refusing to switch')
        self.api('POST', original + '/stop?t=30')
        self.api('POST', original + '/update', {'RestartPolicy': {'Name': 'no', 'MaximumRetryCount': 0}})
        self.api('POST', original + '/rename?name=' + quote(self.previous_name))
        self.api('POST', '/containers/' + self.candidate + '/rename?name=' + quote(self.name))
        self.api('POST', '/containers/' + self.candidate + '/start')

    def rollback(self):
        # Resolve a create whose response might have been lost before receiving Id.
        if self.candidate is None:
            result = run(['docker', 'inspect', self.candidate_name], check=False)
            if result.returncode == 0:
                self.candidate = json.loads(result.stdout)[0]['Id']
        if self.candidate is not None:
            self.api('DELETE', '/containers/' + self.candidate + '?force=true&v=false')
            self.candidate = None
        original = '/containers/' + self.observed['Id']
        current = self.api('GET', original + '/json')
        if current['Name'] != self.observed['Name']:
            self.api('POST', original + '/rename?name=' + quote(self.name))
        self.api('POST', original + '/update', {'RestartPolicy': self.observed['HostConfig']['RestartPolicy']})
        if not current['State']['Running']:
            self.api('POST', original + '/start')
        restored = self.api('GET', original + '/json')
        if not restored['State']['Running'] or restored['Name'] != self.observed['Name']:
            raise RuntimeError('Original Hub restart could not be confirmed')


def hub(args, user):
    if socket.gethostname().split(".")[0].lower() != "decopon":
        raise ValueError("The hub phase must run on DECO PON")
    lock = HUB_ROOT / 'config/.cutwork-setup.lock'
    no_links(lock)
    with lock.open('a') as held:
        os.fchmod(held.fileno(), 0o600)
        try:
            fcntl.flock(held, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError('Another Cutwork Hub setup is already running') from None
        hub_locked(args, user)


def hub_locked(args, user):
    path = pathlib.Path(args.connection or (pathlib.Path(user.pw_dir) / "cutwork-lime-connect.json"))
    no_links(path)
    if path.stat().st_mode & 0o077 or path.stat().st_size > 512 * 1024:
        raise ValueError("Connection file must be private (chmod 600), at most 512 KiB")
    connection = json.loads(path.read_text())
    if connection.get("source") != SOURCE or connection.get("archiveSha256") != ARCHIVE_SHA256:
        raise ValueError("Unexpected deployment source")
    registration = descriptor(connection["descriptor"])
    token = connection["credential"]
    container = "flamoris-mcp-hub"
    observed = json.loads(run(["docker", "inspect", container]).stdout)[0]
    current_env = dict(value.split("=", 1) for value in observed["Config"].get("Env", []) if "=" in value)
    if current_env.get(KEY) not in (None, "", token):
        raise ValueError("Container has a different Cutwork credential; no takeover")
    runtime = HUB_ROOT / "config/runtime.env"
    no_links(runtime)
    old_env = runtime.read_text()
    runtime_stat = runtime.stat()
    updated = append_credential(old_env, token)
    target = HUB_ROOT / "mcps/cutwork-lime.yaml"
    no_links(target)
    registration_bytes = (json.dumps(registration, indent=2) + "\n").encode()
    if target.exists() and json.loads(target.read_text()) != registration:
        raise ValueError("Existing Cutwork descriptor differs; no automatic overwrite")
    previous_target = target.read_bytes() if target.exists() else None
    previous_stat = target.stat() if target.exists() else None
    checked = json.loads(run(["docker", "exec", "-i", container, "python", "-c", VALIDATE],
                             data=json.dumps(registration).encode()).stdout)
    verification = dict(registration, existingTools=checked['existingTools'])
    request = {"key": KEY, "credential": token}
    run(["docker", "exec", "--user", "0", "-i", container, "python", "-c", CONTAINER_ENV],
        data=json.dumps(request).encode())
    swap = None
    if current_env.get(KEY) != token:
        # restart does not reload Docker --env-file. The root filesystem stays
        # read-only; recreate from the same immutable image and complete inspect.
        swap = HubSwap(observed, token)
        daemon = docker_api('GET', '/info')
        if daemon['ID'] != run(['docker', 'info', '--format', '{{.ID}}']).stdout.decode().strip():
            raise ValueError('Docker CLI and local socket refer to different daemons')
    backups = HUB_ROOT / "config" / ("cutwork-setup-backup-" + time.strftime("%Y%m%d-%H%M%S") + "-" + secrets.token_hex(3))
    backups.mkdir(mode=0o700)
    atomic(backups / "runtime.env", old_env.encode())
    atomic(backups / 'container.json', json.dumps(observed).encode())
    if previous_target is not None:
        atomic(backups / 'cutwork-lime.previous', previous_target)
    phase = 'preparing replacement container' if swap else 'updating runtime configuration'
    try:
        if swap:
            swap.prepare()
        phase = 'updating runtime configuration'
        atomic(runtime, updated.encode(), mode=runtime.stat().st_mode & 0o777,
               uid=runtime.stat().st_uid, gid=runtime.stat().st_gid)
        atomic(target, registration_bytes, mode=0o644)
        phase = 'starting Hub with the added connector credential'
        if swap:
            swap.activate()
        else:
            run(["docker", "restart", "--time", "30", container], timeout=60)
        phase = 'verifying the Hub catalog and LIME connection'
        deadline = time.monotonic() + 240
        while time.monotonic() < deadline:
            verified = run(["docker", "exec", "-i", container, "python", "-c", VERIFY],
                           data=json.dumps(verification).encode(), check=False, timeout=25)
            if verified.returncode == 0:
                result = verified.stdout.decode().strip()
                break
            print("Waiting for the LIME connector; no editing request is being sent.", flush=True)
            time.sleep(10)
        else:
            raise RuntimeError('LIME connection was not verified within four minutes')
    except BaseException as failure:
        detail = str(failure) if type(failure) is RuntimeError else type(failure).__name__
        try:
            atomic(runtime, old_env.encode(), runtime_stat.st_mode & 0o777, runtime_stat.st_uid, runtime_stat.st_gid)
            if previous_target is not None:
                atomic(target, previous_target, previous_stat.st_mode & 0o777, previous_stat.st_uid, previous_stat.st_gid)
            elif not target.exists() or target.read_bytes() == registration_bytes:
                target.unlink(missing_ok=True)
            if swap:
                swap.rollback()
            else:
                run(["docker", "restart", "--time", "30", container], timeout=60)
        except BaseException:
            raise RuntimeError('Hub setup failed during ' + phase + '; rollback needs investigation. Protected backup: ' + str(backups)) from None
        raise RuntimeError('Hub setup failed during ' + phase + ' (' + detail + '); original container/runtime/catalog restored. Private connection file retained.') from None
    print(result)
    path.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("role", choices=["lime", "hub"])
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--archive")
    parser.add_argument("--hub-endpoint")
    parser.add_argument("--connection")
    args = parser.parse_args()
    if not args.apply:
        print(json.dumps({"role": args.role, "changes": False, "artifactRun": RUN_ID,
                          "source": SOURCE, "archiveSha256": ARCHIVE_SHA256}))
        return
    if os.geteuid() != 0:
        raise ValueError("Use sudo from the normal operator account")
    user = operator()
    (lime if args.role == "lime" else hub)(args, user)


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, ValueError, KeyError, subprocess.SubprocessError) as error:
        # Only our bounded messages are printable; never echo subprocess payload/traceback.
        detail = str(error) if type(error) in (RuntimeError, ValueError) else type(error).__name__
        print("Setup stopped: " + detail)
        raise SystemExit(1)
