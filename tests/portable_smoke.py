"""Verify the published EXE runs without dotnet on PATH and retains data after relocation."""
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import sqlite3
import subprocess
import time
import urllib.error
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TEST = ROOT / '.test-artifacts' / ('portable-' + uuid.uuid4().hex)
TEST.mkdir(parents=True)
packages = sorted((ROOT / 'dist').glob('WebPrint-win-x64-*.zip'), key=lambda p: p.stat().st_mtime)
if not packages:
    raise RuntimeError('Run scripts/build-portable.ps1 first')
original, moved = TEST / 'original', TEST / 'moved folder 中文'
with zipfile.ZipFile(packages[-1]) as package:
    package.extractall(original)
assert (original / 'dll/coreclr.dll').is_file(), 'Runtime is not bundled in dll/'
assert not list(original.glob('*.dll')), 'DLLs remain in application root'
assert (original / 'lang').is_dir(), 'Language directory is missing'
assert list((original / 'lang').glob('*/*.resources.dll')), 'Localized satellite resources are missing'
assert not list((original / 'dll').glob('*/**/*.resources.dll')), 'Language DLLs remain outside lang/'
assert not (original / 'data/webprint.db').exists(), 'Clean package leaked a database'
password = secrets.token_urlsafe(24)
base = ''

def client():
    return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

def request(opener, path, method='GET', data=None, headers=None):
    h = {'X-WebPrint': '1'}
    if isinstance(data, dict):
        h['Content-Type'] = 'application/json'
        data = json.dumps(data).encode()
    if headers:
        h.update(headers)
    with opener.open(urllib.request.Request(base + path, method=method, data=data, headers=h), timeout=30) as res:
        body = res.read()
        return json.loads(body) if 'application/json' in res.headers.get('Content-Type', '') else body

def start(folder):
    global base
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    env = {k: v for k, v in os.environ.items() if not k.lower().startswith(('storage__', 'printing__', 'aspnetcore_', 'dotnet_', 'webprint_'))}
    env.update(PATH=os.path.join(os.environ['SystemRoot'], 'System32'), DOTNET_ROOT=str(TEST / 'no-dotnet'),
               DOTNET_MULTILEVEL_LOOKUP='0', WEBPRINT_ADMIN_PASSWORD=password, ASPNETCORE_ENVIRONMENT='Production',
               COREHOST_TRACE='1',COREHOST_TRACEFILE=str(TEST / (folder.name + '-host-trace.log')))
    log = (TEST / (folder.name + '.log')).open('w', encoding='utf-8')
    proc = subprocess.Popen([str(folder / 'WebPrint.exe'), '--urls', base], cwd=TEST, env=env, stdout=log, stderr=log)
    for _ in range(100):
        if proc.poll() is not None:
            log.close()
            raise RuntimeError('Portable server exited; see ' + str(TEST))
        try:
            urllib.request.urlopen(base, timeout=1).close()
            trace=(TEST / (folder.name + '-host-trace.log')).read_text(encoding='utf-8',errors='replace')
            assert str(folder / 'dll/hostfxr.dll').lower() in trace.lower(), 'App-local hostfxr was not resolved from dll/'
            return proc, log
        except OSError:
            time.sleep(.2)
    proc.terminate()
    proc.wait(timeout=10)
    log.close()
    raise RuntimeError('Portable startup timeout')

def stop(proc, log):
    proc.terminate()
    proc.wait(timeout=10)
    log.close()

proc, log = start(original)
try:
    admin = client()
    assert b'WebPrint' in request(admin, '/')
    assert b'state' in request(admin, '/app.js')
    request(admin, '/api/login', 'POST', {'username': 'admin', 'password': password})
    request(admin, '/api/users', 'POST', {'username': 'migrateduser', 'password': password, 'role': 'user'})
    fixture = sorted((ROOT / '.test-artifacts').glob('http-*/data/documents/*/preview.pdf'))[-1].read_bytes()
    boundary = 'PortableTestBoundary'
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="portable.pdf"\r\nContent-Type: application/pdf\r\n\r\n'.encode() + fixture + f'\r\n--{boundary}--\r\n'.encode())
    doc = request(admin, '/api/documents', 'POST', body, {'Content-Type': f'multipart/form-data; boundary={boundary}'})
    request(admin, '/api/system')
finally:
    stop(proc, log)
# Simulate an old machine with a queued task; no printer submission is made.
with sqlite3.connect(original / 'data/webprint.db') as db:
    db.execute("UPDATE settings SET value='old-host-for-migration-test' WHERE name='machine'")
    db.execute("INSERT INTO printers VALUES('Migration test printer',1)")
    job_id = uuid.uuid4().hex
    db.execute("INSERT INTO jobs VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", (
        job_id, 'admin', doc['id'], doc['name'], 'Migration test printer', '', 1, 1, 1, 'simplex', 0,
        1, 1, 'queued', None, 'unconfirmed', doc['created'], '127.0.0.1', '', uuid.uuid4().hex))
shutil.copytree(original, moved)
proc, log = start(moved)
try:
    admin = client()
    request(admin, '/api/login', 'POST', {'username': 'admin', 'password': password})
    assert any(u['name'] == 'migrateduser' for u in request(admin, '/api/users'))
    assert request(admin, f"/api/documents/{doc['id']}/pdf").startswith(b'%PDF')
    jobs = request(admin, '/api/jobs')
    assert len(jobs) == 1 and jobs[0]['id'] == job_id and jobs[0]['status'] == 'unknown'
    assert any(ev['action'] == 'machine_changed' for ev in request(admin, '/api/events'))
    with sqlite3.connect(moved / 'data/webprint.db') as db:
        assert db.execute('SELECT count(*) FROM printers WHERE enabled=1').fetchone()[0] == 0
    assert not (TEST / 'data').exists(), 'Data incorrectly depends on shell working directory'
    assert (moved / 'data/webprint.db').exists()
    print('PASS: extracted ZIP runs with no dotnet on PATH, from another working directory; relocation preserves users, files, jobs, audit and pauses old queue')
    print('Tested package:', packages[-1])
    print('Artifacts:', TEST)
finally:
    stop(proc, log)
