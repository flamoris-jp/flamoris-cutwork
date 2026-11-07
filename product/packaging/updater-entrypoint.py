#!/usr/bin/env python3
"""Application-owned supervisor; Updater owns installation, mounts and restart."""
import http.server
import json
import os
import pathlib
import signal
import subprocess
import sys
import threading
import time
from urllib.parse import urlsplit


def settings(environment, state):
    retained = state / 'connection.json'
    if retained.is_symlink() or (retained.exists() and
            (not retained.is_file() or retained.stat().st_mode & 0o077 or retained.stat().st_size > 16384)):
        raise ValueError('Retained connection must be a private regular file')
    old = json.loads(retained.read_text()) if retained.exists() else {}
    if not isinstance(old, dict):
        raise ValueError('Invalid retained connection')
    endpoint = environment.get('CUTWORK_HUB_ENDPOINT') or old.get('endpoint', '')
    credential = environment.get('CUTWORK_HUB_TOKEN') or old.get('credential', '')
    if not isinstance(endpoint, str) or not isinstance(credential, str):
        raise ValueError('Invalid retained connection')
    parsed = urlsplit(endpoint)
    if (parsed.scheme != 'wss' or not parsed.hostname or parsed.username or parsed.password
            or parsed.query or parsed.fragment or not parsed.path.endswith('/desktop/cutwork-lime')
            or not credential.strip() or len(credential) > 4096 or any(ord(c) < 32 for c in credential)):
        raise ValueError('Configure the dedicated Cutwork WSS endpoint and private credential')
    return endpoint, credential


def main():
    os.umask(0o077)
    state = pathlib.Path('/data/state')
    endpoint, credential = settings(os.environ, state)
    port = int(os.environ.get('CUTWORK_PORT', '8775'))
    if not 1 <= port <= 65535:
        raise ValueError('Invalid health port')
    ready, stopping = threading.Event(), threading.Event()
    child = None

    class Health(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path != '/healthz':
                self.send_error(404)
                return
            alive = ready.is_set() and child is not None and child.poll() is None and not stopping.is_set()
            body = json.dumps({'status': 'ready' if alive else 'starting',
                               'hubReadiness': 'verify separately through Hub'}).encode()
            self.send_response(200 if alive else 503)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers(); self.wfile.write(body)
        def log_message(self, *_):
            pass

    server = http.server.ThreadingHTTPServer(('127.0.0.1', port), Health)
    server.daemon_threads = True
    environment = os.environ.copy()
    environment.pop('CUTWORK_HUB_TOKEN', None)
    os.environ.pop('CUTWORK_HUB_TOKEN', None)
    environment['CUTWORK_LIME_CONNECTOR_TOKEN'] = credential
    credential = None

    def stop(*_):
        stopping.set()
        if child is not None and child.poll() is None:
            child.terminate()

    def output():
        for line in child.stdout:
            print(line, end='', flush=True)
            try:
                if json.loads(line).get('status') == 'started':
                    ready.set()
            except (ValueError, AttributeError):
                pass

    signal.signal(signal.SIGTERM, stop); signal.signal(signal.SIGINT, stop)
    serving = False
    try:
        threading.Thread(target=server.serve_forever, kwargs={'poll_interval': .2}, daemon=True).start()
        serving = True
        child = subprocess.Popen(['/app/cutwork-cli', 'serve', '--workspace', str(state),
                                  '--permission', 'edit', '--hub', endpoint], env=environment,
                                 stdout=subprocess.PIPE, text=True)
        environment.pop('CUTWORK_LIME_CONNECTOR_TOKEN', None)
        if stopping.is_set():
            child.terminate()
        threading.Thread(target=output, daemon=True).start()
        deadline = None
        while child.poll() is None:
            if stopping.is_set():
                deadline = deadline or time.monotonic() + 25
                if time.monotonic() > deadline:
                    child.kill()
            time.sleep(.1)
        return 0 if stopping.is_set() and child.returncode == 130 else child.returncode
    finally:
        ready.clear()
        # shutdown() requires serve_forever to have started.
        if serving:
            server.shutdown()
        server.server_close()
        if child is not None and child.poll() is None:
            child.terminate()
            try: child.wait(timeout=25)
            except subprocess.TimeoutExpired: child.kill(); child.wait()


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        print(json.dumps({'error': 'resident_start_failed', 'type': type(error).__name__}), file=sys.stderr)
        sys.exit(1)
