import contextlib
import http.client
import http.server
import importlib.util
import io
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import types
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('nginx_setup', Path(__file__).resolve().parents[2] / 'deploy/linux/setup-cutwork-lime.py')
SETUP = importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(SETUP)
REFERENCE = '''location = /desktop/kachinco-lime {
    proxy_pass http://127.0.0.1:8769;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header Authorization $http_authorization;
    proxy_set_header Origin $http_origin;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_buffering off;
    proxy_read_timeout 180s;
    proxy_send_timeout 180s;
}'''
ORIGINAL = REFERENCE.replace('/desktop/kachinco-lime','/desktop/cutwork') + '\n'


class NginxTests(unittest.TestCase):
    def test_addition_is_idempotent_and_failure_restores_the_original(self):
        for failure in (None, 'test', 'reload'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory); target = root/'cutwork.conf'; reference = root/'kachinco.conf'
                target.write_text(ORIGINAL); reference.write_text(REFERENCE)
                backup = root/'backup'; backup.mkdir()
                calls = []
                def command(args, **kwargs):
                    calls.append(args)
                    if args == ['nginx','-T']:
                        return types.SimpleNamespace(stdout=(''.join('# configuration file '+str(p)+':\n'+p.read_text()+'\n' for p in (target,reference))).encode())
                    if kwargs.get('check',True) and ((failure == 'test' and args == ['nginx','-t']) or
                                                   (failure == 'reload' and args == ['systemctl','reload','nginx'])):
                        raise RuntimeError('fixture failure')
                    return types.SimpleNamespace(stdout=b'')
                with patch.object(SETUP,'NGINX_SNIPPETS',root), \
                     patch.object(SETUP,'run',command), contextlib.redirect_stdout(io.StringIO()):
                    if failure:
                        with self.assertRaises(RuntimeError): SETUP.ensure_nginx_route(backup)
                        self.assertEqual(target.read_text(),ORIGINAL)
                        if failure == 'test': self.assertNotIn(['systemctl','reload','nginx'],calls)
                        else: self.assertEqual(calls[-1],['systemctl','reload','nginx'])
                    else:
                        SETUP.ensure_nginx_route(backup)
                        first = target.read_bytes()
                        SETUP.ensure_nginx_route(backup)
                        self.assertEqual(first,target.read_bytes())
                        self.assertEqual(calls.count(['systemctl','reload','nginx']),1)
                        self.assertEqual(target.read_text().count('location = /desktop/cutwork-lime {'),1)
                        self.assertTrue(target.read_text().startswith(ORIGINAL))
                    self.assertEqual((backup/'nginx-cutwork.conf').read_text(),ORIGINAL)

    def test_missing_includes_or_conflicting_route_do_not_write_or_reload(self):
        for configured in ('no snippets included', 'conflict'):
            with self.subTest(configured=configured), tempfile.TemporaryDirectory() as directory:
                root=Path(directory); target=root/'cutwork.conf'; reference=root/'reference.conf'
                target.write_text(ORIGINAL); reference.write_text(REFERENCE)
                dump = configured if configured != 'conflict' else (
                    '# configuration file '+str(target)+':\n'+ORIGINAL+'\n# configuration file '+str(reference)+':\n'+REFERENCE+
                    '\nlocation = /desktop/cutwork-lime { return 404; }')
                with patch.object(SETUP,'NGINX_SNIPPETS',root), \
                     patch.object(SETUP,'run',return_value=types.SimpleNamespace(stdout=dump.encode())) as command:
                    with self.assertRaises(ValueError): SETUP.ensure_nginx_route(root)
                    self.assertEqual(command.call_count,1)
                    self.assertEqual(target.read_text(),ORIGINAL)

    @unittest.skipUnless(os.environ.get('CUTWORK_TEST_NGINX'), 'Explicit real Nginx route acceptance')
    def test_real_nginx_missing_route_then_authenticated_websocket_upgrade(self):
        self.assertIsNotNone(shutil.which('nginx'))
        observed=[]
        class Upstream(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                observed.append((self.path,dict(self.headers)))
                self.send_response(101); self.send_header('Upgrade','websocket'); self.send_header('Connection','upgrade'); self.end_headers()
            def log_message(self,*args): pass
        server=http.server.ThreadingHTTPServer(('127.0.0.1',8769),Upstream)
        threading.Thread(target=server.serve_forever,daemon=True).start()
        process=None
        try:
            with tempfile.TemporaryDirectory() as directory:
                root=Path(directory); target=root/'cutwork.conf'; reference=root/'kachinco.conf'
                target.write_text(ORIGINAL); reference.write_text(REFERENCE)
                backup=root/'backup'; backup.mkdir()
                with socket.socket() as available:
                    available.bind(('127.0.0.1',0)); port=available.getsockname()[1]
                config=root/'nginx.conf'
                temp_paths=' '.join(name+'_temp_path '+str(root/name)+';' for name in ('client_body','proxy','fastcgi','uwsgi','scgi'))
                config.write_text('pid '+str(root/'nginx.pid')+';\nerror_log '+str(root/'error.log')+';\nevents {}\nhttp { '+temp_paths+' access_log off; server { listen 127.0.0.1:'+str(port)+'; server_name localhost; include '+str(target)+'; include '+str(reference)+'; } }\n')
                def actual(args,**kwargs):
                    mapped=(['nginx','-s','reload'] if args[0]=='systemctl' else args)+['-p',str(root),'-c',str(config)]
                    return subprocess.run(mapped,capture_output=True,check=kwargs.get('check',True),timeout=10)
                def upgrade(route):
                    connection=http.client.HTTPConnection('127.0.0.1',port,timeout=3)
                    try:
                        connection.request('GET','/desktop/'+route,headers={'Host':'localhost','Connection':'Upgrade','Upgrade':'websocket','Authorization':'Bearer fixture-only'})
                        return connection.getresponse().status
                    finally: connection.close()
                process=subprocess.Popen(['nginx','-p',str(root),'-c',str(config),'-g','daemon off;'],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
                for attempt in range(50):
                    try:
                        if upgrade('kachinco-lime')==101: break
                    except OSError: time.sleep(.1)
                else: self.fail('Nginx did not start')
                self.assertEqual(upgrade('cutwork-lime'),404)
                with patch.object(SETUP,'NGINX_SNIPPETS',root), \
                     patch.object(SETUP,'run',actual), contextlib.redirect_stdout(io.StringIO()):
                    SETUP.ensure_nginx_route(backup)
                    for attempt in range(50):
                        if upgrade('cutwork-lime')==101: break
                        time.sleep(.1)
                    else: self.fail('New route did not reach the upstream')
                    SETUP.ensure_nginx_route(backup)
                path,headers=observed[-1]
                self.assertEqual(path,'/desktop/cutwork-lime')
                self.assertEqual(headers.get('Authorization'),'Bearer fixture-only')
                self.assertEqual(headers.get('Upgrade'),'websocket')
                self.assertEqual(headers.get('Host'),'localhost')
                self.assertEqual(headers.get('Connection'),'upgrade')
        finally:
            if process:
                process.terminate()
                try: process.wait(timeout=5)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
            server.shutdown(); server.server_close()


if __name__=='__main__': unittest.main()

