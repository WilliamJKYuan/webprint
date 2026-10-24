"""HTTP 集成验证，不发送任何实机打印任务。只使用 Python 标准库。"""
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid
import zlib
import struct

ROOT = Path(__file__).resolve().parents[1]
TEST = ROOT / '.test-artifacts' / ('http-' + uuid.uuid4().hex)
TEST.mkdir(parents=True)
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    PORT = sock.getsockname()[1]
BASE = f'http://127.0.0.1:{PORT}'
PASSWORD = secrets.token_urlsafe(24)
env = dict(os.environ, Storage__Path=str(TEST / 'data'), WEBPRINT_ADMIN_PASSWORD=PASSWORD,
           ASPNETCORE_ENVIRONMENT='Production', Printing__Enabled='false')
log = (TEST / 'server.log').open('w', encoding='utf-8')
process = subprocess.Popen(['dotnet', str(ROOT / 'bin/Debug/net9.0/WebPrint.dll'), '--urls', BASE], cwd=ROOT, env=env, stdout=log, stderr=log)

def client():
    return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

def request(opener, path, method='GET', data=None, headers=None, expected=200):
    h = {'X-WebPrint': '1'}
    if isinstance(data, dict):
        data = json.dumps(data).encode()
        h['Content-Type'] = 'application/json'
    if headers:
        h.update(headers)
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=h)
    try:
        res = opener.open(req, timeout=30)
    except urllib.error.HTTPError as err:
        res = err
    body = res.read()
    assert res.code == expected, (path, res.code, body.decode(errors='replace'))
    return json.loads(body) if 'application/json' in res.headers.get('Content-Type', '') else body

def upload(opener, filename, content, expected=200):
    boundary = 'WebPrintTestBoundary'
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{filename}"\r\nContent-Type: application/octet-stream\r\n\r\n'.encode() + content + f'\r\n--{boundary}--\r\n'.encode())
    return request(opener, '/api/documents', 'POST', body, {'Content-Type': f'multipart/form-data; boundary={boundary}'}, expected)

try:
    anonymous = client()
    for _ in range(80):
        if process.poll() is not None:
            raise RuntimeError('Server exited; see ' + str(TEST / 'server.log'))
        try:
            request(anonymous, '/')
            break
        except (OSError, urllib.error.URLError):
            time.sleep(.25)
    else:
        raise RuntimeError('Server startup timeout')
    assert b'WebPrint' in request(anonymous, '/')
    request(anonymous, '/api/jobs', expected=401)
    request(anonymous, '/api/login', 'POST', {'username': 'admin', 'password': 'bad'}, expected=401)
    admin = client()
    request(admin, '/api/login', 'POST', {'username': 'admin', 'password': PASSWORD})
    assert request(admin, '/api/me')['role'] == 'admin'
    request(admin, '/api/users', 'POST', {'username': 'alice', 'password': PASSWORD, 'role': 'user'})
    request(admin, '/api/users', 'POST', {'username': 'bob', 'password': PASSWORD, 'role': 'user'})
    alice, bob = client(), client()
    for opener, name in [(alice, 'alice'), (bob, 'bob')]:
        request(opener, '/api/login', 'POST', {'username': name, 'password': PASSWORD})
    request(alice, '/api/users', expected=403)
    request(alice, '/api/reports.csv', expected=403)
    request(alice, '/api/events', expected=403)
    request(alice, '/api/password', 'POST', {'current': PASSWORD, 'password': PASSWORD}, {'X-WebPrint': ''}, 400)
    upload(alice, 'malware.exe', b'not allowed', 400)
    upload(alice, 'broken.pdf', b'not a PDF', 400)
    # 2x2 PNG，验证图片真实解码和 PDF 生成。
    def chunk(tag, data):
        return struct.pack('!I', len(data)) + tag + data + struct.pack('!I', zlib.crc32(tag + data) & 0xffffffff)
    png = (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('!2I5B', 2, 2, 8, 2, 0, 0, 0))
           + chunk(b'IDAT', zlib.compress((b'\x00' + b'\x20\x80\x60' * 2) * 2)) + chunk(b'IEND', b''))
    doc = upload(alice, 'image.png', png)
    assert doc['pages'] == 1 and len(doc['hash']) == 64
    pdf = request(alice, f"/api/documents/{doc['id']}/pdf")
    assert pdf.startswith(b'%PDF')
    request(bob, f"/api/documents/{doc['id']}/pdf", expected=403)
    request(admin, f"/api/documents/{doc['id']}/pdf")
    pdf_doc = upload(alice, 'copied.pdf', pdf)
    assert pdf_doc['pages'] == 1
    assert request(alice, '/api/jobs') == []
    assert request(bob, '/api/jobs') == []
    report = request(admin, '/api/reports.csv')
    assert report.startswith(b'\xef\xbb\xbf')
    request(alice, '/api/password', 'POST', {'current': PASSWORD, 'password': 'short'}, expected=400)
    request(alice, '/api/password', 'POST', {'current': PASSWORD, 'password': PASSWORD + '-new'})
    request(alice, '/api/logout', 'POST')
    request(alice, '/api/me', expected=401)
    events = request(admin, '/api/events')
    assert any(e['action'] == 'upload' for e in events)
    assert any(e['action'] == 'password_changed' for e in events)
    print('PASS: HTTP login, authorization, CSRF header, invalid files, PNG/PDF conversion, document ownership, CSV, password, logout, audit')
    print('Artifacts:', TEST)
finally:
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    log.close()
