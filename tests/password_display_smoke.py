"""Verify local password display across restarts without exposing test credentials in output."""
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
TEST = ROOT / '.test-artifacts' / ('password-' + uuid.uuid4().hex)
TEST.mkdir(parents=True)
initial, changed = secrets.token_hex(3), secrets.token_hex(3)
base = ''
round_number = 0
env = dict(os.environ, Storage__Path=str(TEST / 'data'), WEBPRINT_ADMIN_PASSWORD=initial,
           ASPNETCORE_ENVIRONMENT='Production', Printing__Enabled='false')

def client():
    return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

def request(opener, path, method='GET', data=None, expected=200):
    headers = {'X-WebPrint': '1'}
    if data is not None:
        data = json.dumps(data).encode()
        headers['Content-Type'] = 'application/json'
    try:
        response = opener.open(urllib.request.Request(base + path, method=method, data=data, headers=headers), timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    body = response.read()
    assert response.code == expected, f'{path}: unexpected HTTP status {response.code}'
    return body

def start(expected_password):
    global base, round_number
    round_number += 1
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    log_path = TEST / f'start-{round_number}.log'
    log = log_path.open('w', encoding='utf-8')
    proc = subprocess.Popen(['dotnet', str(ROOT / 'bin/Debug/net9.0/WebPrint.dll'), '--urls', base], cwd=ROOT, env=env, stdout=log, stderr=log)
    try:
        for _ in range(100):
            if proc.poll() is not None:
                raise RuntimeError('Server exited; inspect local test artifacts')
            try:
                urllib.request.urlopen(base, timeout=1).close()
                break
            except OSError:
                time.sleep(.2)
        else:
            raise RuntimeError('Server startup timeout')
        lines = log_path.read_text(encoding='utf-8').splitlines()
        passwords = [line.removeprefix('admin 密码：') for line in lines if line.startswith('admin 密码：')]
        assert passwords == ([] if expected_password is None else [expected_password]), 'Unexpected startup password display'
        return proc, log
    except BaseException:
        proc.terminate()
        proc.wait(timeout=10)
        log.close()
        raise

def stop(proc, log):
    proc.terminate()
    proc.wait(timeout=10)
    log.close()

proc, log = start(initial)
try:
    admin = client()
    request(admin, '/api/login', 'POST', {'username': 'admin', 'password': initial})
    request(admin, '/api/users', 'POST', {'username': 'sixuser', 'password': initial, 'role': 'user'})
    request(admin, '/api/users', 'POST', {'username': 'fiveuser', 'password': 'abcde', 'role': 'user'}, expected=400)
    request(client(), '/api/login', 'POST', {'username': 'sixuser', 'password': initial})
    request(admin, '/api/password', 'POST', {'current': initial, 'password': 'abcde'}, expected=400)
    request(admin, '/api/password', 'POST', {'current': initial, 'password': changed})
    request(client(), '/api/login', 'POST', {'username': 'admin', 'password': initial}, expected=401)
    request(client(), '/api/login', 'POST', {'username': 'admin', 'password': changed})
    for path in ['/api/me', '/api/users', '/api/events', '/api/jobs', '/api/reports.csv']:
        response = request(admin, path).decode('utf-8-sig')
        assert initial not in response and changed not in response, 'Credential leaked through HTTP'
    with sqlite3.connect(TEST / 'data/webprint.db') as db:
        protected = db.execute("SELECT value FROM settings WHERE name='admin_startup_password'").fetchone()[0]
        assert initial not in protected and changed not in protected, 'Display password stored as plaintext'
finally:
    stop(proc, log)

proc, log = start(changed)
try:
    request(client(), '/api/login', 'POST', {'username': 'admin', 'password': changed})
finally:
    stop(proc, log)
# Simulate an older database that contains only password hashes.
with sqlite3.connect(TEST / 'data/webprint.db') as db:
    db.execute("DELETE FROM settings WHERE name='admin_startup_password'")
proc, log = start(None)
try:
    request(client(), '/api/login', 'POST', {'username': 'admin', 'password': changed})
finally:
    stop(proc, log)
proc, log = start(changed)
try:
    request(client(), '/api/login', 'POST', {'username': 'admin', 'password': changed})
finally:
    stop(proc, log)
# Corruption must not reset the account or prevent service startup.
with sqlite3.connect(TEST / 'data/webprint.db') as db:
    db.execute("UPDATE settings SET value='invalid-protected-payload' WHERE name='admin_startup_password'")
proc, log = start(None)
try:
    request(client(), '/api/login', 'POST', {'username': 'admin', 'password': changed})
finally:
    stop(proc, log)
print('PASS: six-character bootstrap/create/change/login, five-character rejection, encrypted local copy, restart display, legacy upgrade without reset, corrupt copy recovery, no HTTP credential exposure')
print('Artifacts contain sensitive test logs; keep local:', TEST)
