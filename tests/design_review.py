"""Run the supplied taste inspector against real authenticated screens in an isolated server.
The local inspector copy only adds authentication/navigation, leaving its measurements unchanged.
"""
import argparse
import os
from pathlib import Path
import secrets
import socket
import subprocess
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--phase', default='after')
parser.add_argument('--screens', default='login,print,jobs,admin')
args = parser.parse_args()
skill = Path(os.environ['USERPROFILE']) / '.agents/skills/design-taste-frontend'
out = ROOT / '.taste-skill' / args.phase
out.mkdir(parents=True, exist_ok=True)
source = (skill / 'scripts/inspect.mjs').read_text(encoding='utf-8')
# Isolate enterprise/user browser extensions; they can inject colours outside the application CSS.
source = source.replace('const browser = await Browser.launch();', 'const browser = await Browser.launch({extraArgs:["--disable-extensions"]});')
for module in ['cdp', 'probe', 'rules']:
    source = source.replace(f"'./lib/{module}.mjs'", repr((skill / f'scripts/lib/{module}.mjs').as_uri()))
anchor = "    await sleep(600); // about 1.2s after the load event: is the title there to read yet?"
assert anchor in source
source = source.replace(anchor, """
    if (process.env.WEBPRINT_REVIEW_SCREEN !== 'login') {
      await page.eval(async (credentials) => {
        const result = await fetch('/api/login', {method:'POST', headers:{'Content-Type':'application/json','X-WebPrint':'1'},body:JSON.stringify({username:'admin',password:credentials.password})});
        if(!result.ok) throw new Error('Review login failed');
        await enter(await result.json()); tab(credentials.screen);
      }, { password:process.env.WEBPRINT_REVIEW_PASSWORD,screen:process.env.WEBPRINT_REVIEW_SCREEN });
      for(let i=0;i<100;i++) {
        const ready = await page.eval(() => document.querySelector('#printer-message').textContent.length>0 &&
          (document.querySelector('#tab-admin').hidden || document.querySelector('#diagnostics span')!==null));
        if(ready)break; await sleep(250);
      }
    }
""" + anchor)
inspector = ROOT / '.taste-skill/inspect-auth.mjs'
source = source.replace('    const after = await page.eval(probe).catch(() => metrics);', '''
    writeFileSync(join(outDir, `browser-state-${w}.json`), JSON.stringify(await page.eval(() => ({
      forcedColors:matchMedia('(forced-colors: active)').matches,
      background:getComputedStyle(document.body).backgroundColor,
      bodyStyle:document.body.getAttribute('style'),
      stylesheets:[...document.styleSheets].map(s=>({href:s.href,disabled:s.disabled,inline:s.href?null:s.ownerNode?.outerHTML.slice(0,1500)}))
    })),null,2));
    const after = await page.eval(probe).catch(() => metrics);''')
inspector.write_text(source, encoding='utf-8')
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
url = f'http://127.0.0.1:{port}'
secret = secrets.token_urlsafe(24)
env = dict(os.environ, Storage__Path=str(out / 'server-data'), WEBPRINT_ADMIN_PASSWORD=secret,
           Printing__Enabled='false', ASPNETCORE_ENVIRONMENT='Production', WEBPRINT_REVIEW_PASSWORD=secret,
           CHROME_PATH=r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe')
log = (out / 'server-private.log').open('w', encoding='utf-8')
server = subprocess.Popen(['dotnet', str(ROOT / 'bin/Debug/net9.0/WebPrint.dll'), '--urls', url], cwd=ROOT, env=env, stdout=log, stderr=log)
try:
    for _ in range(100):
        if server.poll() is not None: raise RuntimeError('Review server exited')
        try:
            urllib.request.urlopen(url,timeout=1).close()
            break
        except OSError: time.sleep(.2)
    else: raise RuntimeError('Review startup timeout')
    for screen in args.screens.split(','):
        target = out / screen
        target.mkdir(exist_ok=True)
        inspect_env = dict(env, WEBPRINT_REVIEW_SCREEN=screen)
        with (target / 'inspector.log').open('w',encoding='utf-8') as inspect_log:
            # Explicit reference brief requests glass; the entry page also takes the reference's larger title.
            allowed = 'glass,app-title-size' if screen == 'login' else 'glass'
            result = subprocess.run(['node',str(inspector),url,'--app','--allow',allowed,'--out',str(target)],cwd=ROOT,env=inspect_env,stdout=inspect_log,stderr=inspect_log,timeout=240)
        print(f'{args.phase}/{screen}: inspector exit {result.returncode}; see {target}')
finally:
    server.terminate()
    server.wait(timeout=15)
    log.close()
