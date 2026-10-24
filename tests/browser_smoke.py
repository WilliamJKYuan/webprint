"""使用本机 Edge 检查 UI；不发送任何打印任务。"""
import os
from pathlib import Path
import secrets
import sqlite3
import socket
import subprocess
import sys
import time
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.test-tools'))
from playwright.sync_api import sync_playwright, expect

TEST = ROOT / '.test-artifacts' / ('browser-' + uuid.uuid4().hex)
TEST.mkdir(parents=True)
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
base = f'http://127.0.0.1:{port}'
password = secrets.token_urlsafe(24)
env = dict(os.environ, Storage__Path=str(TEST / 'data'), WEBPRINT_ADMIN_PASSWORD=password, Printing__Enabled='false')
log = (TEST / 'server.log').open('w', encoding='utf-8')
server = subprocess.Popen(['dotnet', str(ROOT / 'bin/Debug/net9.0/WebPrint.dll'), '--urls', base], cwd=ROOT, env=env, stdout=log, stderr=log)
try:
    for _ in range(100):
        try:
            urllib.request.urlopen(base, timeout=1).close()
            break
        except OSError:
            time.sleep(.2)
    else:
        raise RuntimeError('Server did not start')
    with sync_playwright() as p:
        browser = p.chromium.launch(channel='msedge', headless=True)
        page = browser.new_page(viewport={'width': 1440, 'height': 1000}, device_scale_factor=1)
        errors = []
        page.on('pageerror', lambda err: errors.append(str(err)))
        page.goto(base)
        page.locator('#login-form').wait_for()
        page.screenshot(path=str(TEST / 'login-desktop.png'), full_page=True)
        page.locator('#login-form [name=username]').fill('admin')
        page.locator('#login-form [name=password]').fill(password)
        page.locator('#login-form button').click()
        page.locator('#workspace').wait_for(state='visible')
        expect(page.locator('#printer-message')).not_to_have_text('')
        page.screenshot(path=str(TEST / 'print-desktop.png'), full_page=True)
        page.locator('[data-tab=jobs]').click()
        page.locator('#jobs-empty').wait_for(state='visible')
        page.locator('.date-filter summary').click()
        page.locator('#date-from').fill('2026-01-01')
        page.locator('#date-to').fill('2026-12-31')
        assert '0' in page.locator('#job-count').inner_text()
        page.locator('.date-filter summary').click()
        page.screenshot(path=str(TEST / 'audit-desktop.png'), full_page=True)
        page.locator('[data-tab=admin]').click()
        page.locator('#diagnostics span').first.wait_for()
        page.screenshot(path=str(TEST / 'admin-desktop.png'), full_page=True)
        # 创建账户并核对日志面板。
        page.locator('#user-form [name=username]').fill('browseruser')
        page.locator('#user-form [name=password]').fill(password)
        page.locator('#user-form button').click()
        page.get_by_text('browseruser · 用户', exact=True).wait_for()
        page.locator('[data-tab=print]').click()
        # 用 HTTP 测试留下的真实 PNG->PDF 文件验证网页上传。
        fixtures = list((ROOT / '.test-artifacts').glob('http-*/data/documents/*/preview.pdf'))
        if not fixtures:
            raise RuntimeError('Run tests/http_smoke.py first')
        page.locator('#file').set_input_files(str(fixtures[-1]))
        page.locator('#file-info').wait_for(state='visible')
        assert '1 页' in page.locator('#file-meta').inner_text()
        assert '1 页' in page.locator('#estimate-pages').inner_text()
        page.locator('#copies').fill('3')
        page.locator('#duplex').select_option('duplex')
        assert '3 张纸' in page.locator('#estimate-sheets').inner_text()
        page.screenshot(path=str(TEST / 'preview-desktop.png'), full_page=True)
        # Backed by isolated test rows only; no print submission and no production sample data.
        document = page.evaluate('() => ({id:state.doc.id,name:state.doc.name,created:state.doc.created})')
        with sqlite3.connect(TEST / 'data/webprint.db') as db:
            for status, copies, actual in [('unknown', 2, None), ('reviewed', 1, 1), ('failed', 1, None), ('cancelled', 1, None)]:
                db.execute('INSERT INTO jobs VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)', (
                    uuid.uuid4().hex, 'admin', document['id'], document['name'], 'UI review printer', '', 1, 1, copies,
                    'simplex', 0, copies, copies, status, actual, 'manual' if actual is not None else 'unconfirmed',
                    document['created'], '127.0.0.1', 'Isolated UI test; no physical printing.', uuid.uuid4().hex))
        page.locator('[data-tab=jobs]').click()
        expect(page.locator('#job-rows tr')).to_have_count(4)
        assert page.locator('#metric-pages').inner_text() == '3'
        assert page.locator('#metric-actual').inner_text() == '1'
        assert page.locator('#metric-pending').inner_text() == '1'
        page.screenshot(path=str(TEST / 'audit-populated-desktop.png'), full_page=True)
        page.locator('#status-filter').select_option('unknown')
        expect(page.locator('#job-rows tr')).to_have_count(1)
        assert page.locator('#metric-pages').inner_text() == '2'
        page.locator('#status-filter').select_option('')
        page.set_viewport_size({'width': 390, 'height': 844})
        page.screenshot(path=str(TEST / 'audit-populated-mobile.png'), full_page=True)
        assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth'), 'Mobile audit overflows'
        page.locator('#job-rows tr').filter(has_text='结果待确认').get_by_text('登记结果', exact=True).click()
        page.locator('#review-form [name=actualPages]').fill('0')
        page.locator('#review-form [name=note]').fill('Isolated UI review, no physical printing.')
        page.locator('#review-form button.primary').click()
        expect(page.locator('#metric-pending')).to_have_text('0')
        assert page.locator('#metric-actual').inner_text() == '1'
        page.locator('[data-tab=print]').click()
        page.screenshot(path=str(TEST / 'print-mobile.png'), full_page=True)
        assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth'), 'Mobile page overflows'
        page.locator('#mobile-account').click()
        assert page.locator('#mobile-account').get_attribute('aria-expanded') == 'true'
        page.locator('#password-open').click()
        page.locator('#password-dialog').wait_for(state='visible')
        assert page.locator('#password-form [name=password]').get_attribute('minlength') == '6'
        page.locator('#password-form [name=current]').fill(password)
        page.locator('#password-form [name=password]').fill('Pass06')
        page.locator('#password-form button.primary').click()
        page.locator('#password-dialog').wait_for(state='hidden')
        page.locator('#mobile-account').click()
        page.locator('#logout').click()
        page.locator('#login').wait_for(state='visible')
        page.screenshot(path=str(TEST / 'login-mobile.png'), full_page=True)
        assert not errors, errors
        browser.close()
        print('PASS: Edge UI login, navigation, date filters, user creation, PDF upload, estimates, mobile account menu, six-character password change, logout; no JavaScript errors')
        print('Screenshots:', TEST)
finally:
    server.terminate()
    try:
        server.wait(timeout=10)
    except subprocess.TimeoutExpired:
        server.kill()
        server.wait()
    log.close()
