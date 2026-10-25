// Real Edge + isolated backend. Requires Node 22 and the local taste skill's CDP helper.
// No print jobs are created. Private logs and screenshots stay in the OS temp directory.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtempSync, openSync, closeSync } from 'node:fs';
import { tmpdir, homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { randomBytes } from 'node:crypto';
const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const helper = join(homedir(), '.agents/skills/design-taste-frontend/scripts/lib/cdp.mjs');
const { Browser, sleep } = await import(pathToFileURL(helper).href);
const output = mkdtempSync(join(tmpdir(), 'webprint-motion-'));
const password = randomBytes(24).toString('hex');
const log = openSync(join(output, 'server-private.log'), 'w');
const packagedRoot = process.argv[2] && resolve(process.argv[2]);
const executable = packagedRoot ? join(packagedRoot, 'WebPrint.exe') : 'dotnet';
const arguments_ = packagedRoot ? [] : [join(root, 'bin/Debug/net9.0/WebPrint.dll')];
const server = spawn(executable, [...arguments_, '--urls', 'http://127.0.0.1:0'], {
  cwd: root, env: { ...process.env, Storage__Path: join(output, 'data'),
    WEBPRINT_ADMIN_PASSWORD: password, Printing__Enabled: 'false' }, stdio: ['ignore', 'pipe', log]
});
let base, browser;
// Write startup output privately, extracting only the loopback address.
const { writeSync } = await import('node:fs');
server.stdout.on('data', chunk => {
  writeSync(log, chunk);
  base ??= chunk.toString().match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1];
});
try {
  for (let i = 0; !base && i < 100; i++) {
    if (server.exitCode !== null) throw new Error('Backend exited; see private log');
    await sleep(200);
  }
  assert.ok(base, 'backend is ready');
  browser = await Browser.launch({ extraArgs: ['--disable-extensions'] });
  const page = await browser.newPage();
  await page.goto(base);
  await page.eval(async password => {
    await enter(await api('/login', 'POST', { username: 'admin', password }));
  }, password);
  for (const width of [390, 768, 1440]) {
    await page.setViewport({ width, height: 900, mobile: width < 600 });
    await sleep(120);
    await page.eval(() => document.querySelector('[data-tab=jobs]').click());
    const during = await page.eval(() => ({
      moving: document.querySelector('.sidebar nav').getAnimations({ subtree: true }).some(a => a.playState === 'running'),
      visible: [...document.querySelectorAll('.tab-panel')].filter(p => !p.hidden).length
    }));
    assert.ok(during.moving, `navigation moves at ${width}`);
    assert.equal(during.visible, 1);
    // Rapid input must leave one visible panel and the indicator on the last button.
    await page.eval(() => { for (const name of ['admin', 'print', 'jobs', 'print']) document.querySelector(`[data-tab=${name}]`).click(); });
    await sleep(400);
    const geometry = await page.eval(() => {
      const nav = document.querySelector('.sidebar nav'), active = nav.querySelector('.active');
      const style = getComputedStyle(nav, '::before'), matrix = new DOMMatrix(style.transform);
      return { x: matrix.m41, y: matrix.m42, w: parseFloat(style.width), h: parseFloat(style.height),
        left: active.offsetLeft, top: active.offsetTop, width: active.offsetWidth, height: active.offsetHeight,
        overflow: document.documentElement.scrollWidth > innerWidth,
        current: active.getAttribute('aria-current'), visible: !document.querySelector('#tab-print').hidden };
    });
    assert.ok(Math.abs(geometry.x - geometry.left) < 1 && Math.abs(geometry.y - geometry.top) < 1);
    assert.ok(Math.abs(geometry.w - geometry.width) < 1 && Math.abs(geometry.h - geometry.height) < 1);
    assert.equal(geometry.overflow, false);
    assert.equal(geometry.current, 'page'); assert.equal(geometry.visible, true);
    await page.screenshot({ path: join(output, `print-${width}.png`), format: 'png' });
    if (width <= 1100) {
      const header = await page.eval(() => {
        const bar = document.querySelector('.sidebar');
        const nav = bar.querySelector('nav');
        const before = nav.getBoundingClientRect().bottom;
        scrollTo(0, 260);
        return { filter: getComputedStyle(bar, '::before').backdropFilter,
          top: bar.getBoundingClientRect().top, before, after: nav.getBoundingClientRect().bottom };
      });
      assert.ok(header.filter.includes('blur(24px)'), 'mobile header has backdrop blur');
      assert.ok(header.top >= 0 && header.top <= 16, 'header stays at viewport top');
      assert.ok(Math.abs(header.before - header.after) < 1 && header.after > 800,
        'bottom navigation remains anchored to viewport after scroll');
      await sleep(100);
      await page.screenshot({ path: join(output, `header-scrolled-${width}.png`), format: 'png' });
      await page.eval(() => scrollTo(0, 0));
    }
    await page.eval(() => document.querySelector('#password-open').click());
    await sleep(280);
    assert.equal(await page.eval(() => document.querySelector('#password-dialog').matches(':modal')), true);
    await page.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await sleep(220);
    assert.equal(await page.eval(() => document.querySelector('#password-dialog').open), false);
  }
  await page.setViewport({ width: 390, height: 844, mobile: true });
  await page.eval(() => {
    document.querySelector('#mobile-account').click();
    document.querySelector('#password-open').click();
  });
  await sleep(300);
  await page.screenshot({ path: join(output, 'dialog-mobile.png'), format: 'png' });
  await page.eval(() => document.querySelector('#password-close').click());
  await sleep(200);
  await page.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }] });
  await page.eval(() => document.querySelector('[data-tab=jobs]').click());
  assert.equal(await page.eval(() => document.getAnimations().filter(a => a.playState === 'running').length), 0);
  // Re-login as a regular user: two buttons must each get half of the available width.
  await page.eval(async password => {
    await api('/users', 'POST', { username: 'motionuser', password, role: 'user' });
    await api('/logout', 'POST'); showLogin();
    await enter(await api('/login', 'POST', { username: 'motionuser', password }));
  }, password);
  await sleep(120);
  const user = await page.eval(() => {
    const nav = document.querySelector('.sidebar nav');
    const buttons = [...nav.querySelectorAll('button')].filter(b => !b.hidden);
    return { count: buttons.length, widths: buttons.map(b => b.offsetWidth),
      indicator: parseFloat(getComputedStyle(nav, '::before').width) };
  });
  assert.equal(user.count, 2); assert.ok(Math.abs(user.widths[0] - user.widths[1]) <= 1);
  assert.ok(Math.abs(user.indicator - user.widths[0]) <= 1);
  await page.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-transparency', value: 'reduce' }] });
  assert.equal(await page.eval(() => getComputedStyle(document.querySelector('.sidebar'), '::before').backdropFilter), 'none');
  assert.deepEqual(page.consoleErrors, []);
  console.log(`PASS: responsive navigation, rapid switching, modal Escape, reduced motion, user role; screenshots: ${output}`);
} finally {
  if (browser) await browser.close();
  server.kill();
  if (server.exitCode === null) await new Promise(resolve => server.once('exit', resolve));
  closeSync(log);
}
