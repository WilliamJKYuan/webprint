"""Exercise actual WinForms buttons and server lifecycle in an isolated published ZIP."""
import json
import os
from pathlib import Path
import subprocess
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TEST = ROOT / '.test-artifacts' / ('desktop-' + uuid.uuid4().hex)
TEST.mkdir(parents=True)
packages = sorted((ROOT / 'dist').glob('WebPrint-win-x64-*.zip'), key=lambda p: p.stat().st_mtime)
if not packages:
    raise RuntimeError('Build portable package first')
with zipfile.ZipFile(packages[-1]) as package:
    package.extractall(TEST / 'app')
app = TEST / 'app'
assert (app / 'WebPrint.Desktop.exe').exists(), 'Desktop launcher is missing'
assert not list(app.glob('*.dll')), 'Application root contains DLL files'
assert (app / 'dll/WebPrint.Desktop.dll').exists()
env = {k: v for k, v in os.environ.items() if not k.lower().startswith(('storage__', 'printing__', 'aspnetcore_', 'dotnet_', 'webprint_'))}
env.update(PATH=os.path.join(os.environ['SystemRoot'], 'System32'), DOTNET_ROOT=str(TEST / 'no-dotnet'),
           DOTNET_MULTILEVEL_LOOKUP='0', WEBPRINT_DESKTOP_TEST_ROOT=str(TEST / 'results'))
process = subprocess.Popen([str(app / 'WebPrint.Desktop.exe'), '--smoke-test'], cwd=TEST, env=env)
try:
    process.wait(timeout=150)
except subprocess.TimeoutExpired:
    subprocess.run(['taskkill.exe', '/PID', str(process.pid), '/T', '/F'], capture_output=True)
    raise
result = json.loads((TEST / 'results/result.json').read_text(encoding='utf-8'))
assert result['passed'], result
assert process.returncode == 0, process.returncode
print('PASS: desktop live printing policy, settings, tray minimize/restore/exit, organized DLL/language loading, port, startup/stop/restart, persistence (no print jobs submitted)')
print('Checks:', len(result['checks']))
print('Tested package:', packages[-1])
print('Screenshot and results:', TEST / 'results')
