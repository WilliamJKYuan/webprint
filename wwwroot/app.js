'use strict';
const $ = s => document.querySelector(s);
const state = { me: null, doc: null, printers: [], jobs: [], requestId: null, reviewId: null, uploading: false, submitting: false };
const statuses = { queued: '等待打印', dispatching: '正在发送', submitted_unverified: '已发送，待确认', unknown: '结果待确认', failed: '发送失败', cancelled: '已取消', reviewed: '已核对' };
const eventNames = { login: '登录', password_changed: '修改密码', printer_changed: '修改打印机共享', user_created: '创建账户', upload: '上传文件', printing_changed: '切换实机打印', converter_changed: '切换文档转换软件', machine_changed: '部署或迁移服务', content_purged: '清理到期文件', ...statuses };
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
const navigation = $('.sidebar nav');
const transitions = new Map();
function reveal(element, distance = 8) {
  transitions.get(element)?.cancel();
  if (reducedMotion.matches || !element.animate) return;
  const animation = element.animate([
    { opacity: .3, transform: `translateY(${distance}px)` },
    { opacity: 1, transform: 'translateY(0)' }
  ], { duration: 240, easing: 'cubic-bezier(.22, 1, .36, 1)' });
  transitions.set(element, animation);
  animation.finished.catch(() => {}).finally(() => {
    if (transitions.get(element) === animation) transitions.delete(element);
  });
}
reducedMotion.addEventListener('change', () => {
  for (const animation of transitions.values()) animation.cancel();
  transitions.clear();
});
function positionNavigation() {
  const active = navigation.querySelector('button.active');
  if (!active || !active.getClientRects().length) return;
  // Local offsets stay correct while the mobile navigation itself is translated.
  navigation.style.setProperty('--nav-x', active.offsetLeft + 'px');
  navigation.style.setProperty('--nav-y', active.offsetTop + 'px');
  navigation.style.setProperty('--nav-width', active.offsetWidth + 'px');
  navigation.style.setProperty('--nav-height', active.offsetHeight + 'px');
  navigation.classList.add('indicator-ready');
}
new ResizeObserver(() => {
  // A breakpoint/role change should reposition, rather than slide from the old layout.
  navigation.classList.remove('indicator-animated');
  positionNavigation();
}).observe(navigation);
document.fonts.ready.then(positionNavigation);
let toastTimer;
function toast(message, error = false) { clearTimeout(toastTimer); $('#toast').textContent = message; $('#toast').className = error ? 'error' : ''; $('#toast').hidden = false; toastTimer = setTimeout(() => $('#toast').hidden = true, 6000); }
async function api(path, method = 'GET', body) {
  const options = { method, headers: { 'X-WebPrint': '1' } };
  if (body instanceof FormData) options.body = body;
  else if (body !== undefined) { options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(body); }
  const res = await fetch('/api' + path, options);
  if (res.status === 401 && path !== '/login') showLogin();
  if (!res.ok) { const data = await res.json().catch(() => ({})); throw new Error(data.error || (res.status === 429 ? '尝试过于频繁，请一分钟后再试。' : `请求失败 (${res.status})`)); }
  return res.headers.get('content-type')?.includes('json') ? res.json() : null;
}
function text(tag, value, cls) { const el = document.createElement(tag); el.textContent = value; if (cls) el.className = cls; return el; }
function date(value) { return new Date(value).toLocaleString('zh-CN', { hour12: false }); }
function screenHeading(loggedIn) {
  for (const [id, tag] of [['login-title', loggedIn ? 'h2' : 'h1'], ['page-title', loggedIn ? 'h1' : 'h2']]) {
    const old = $('#' + id); if (old.tagName.toLowerCase() === tag) continue;
    const heading = document.createElement(tag); for (const attr of old.attributes) heading.setAttribute(attr.name, attr.value);
    heading.append(...old.childNodes); old.replaceWith(heading);
  }
}
function closeAccountMenu() { $('#account-menu').classList.remove('is-open'); $('#mobile-account').setAttribute('aria-expanded', 'false'); }
function showLogin() { screenHeading(false); closeAccountMenu(); state.me = null; state.doc = null; state.jobs = []; state.requestId = null; $('#workspace').hidden = true; $('#login').hidden = false; $('#preview').removeAttribute('src'); $('#preview').hidden = true; $('#file-info').hidden = true; $('#preview-empty').hidden = false; $('#job-rows').replaceChildren(); $('#event-rows').replaceChildren(); $('#user-list').replaceChildren(); $('#printer-list').replaceChildren(); $('#file').value = ''; $('#print-form').reset(); updateEstimate(); }
async function enter(me) {
  screenHeading(true);
  state.me = me; $('#login').hidden = true; $('#workspace').hidden = false;
  $('#identity').textContent = `${me.username} · ${me.role === 'admin' ? '管理员' : '普通用户'}`;
  document.querySelectorAll('.admin-only').forEach(el => el.hidden = me.role !== 'admin');
  tab('print'); await loadPrinters();
}
function tab(name) {
  if (name === 'admin' && state.me?.role !== 'admin') return;
  const previous = navigation.querySelector('button.active')?.dataset.tab;
  const changed = previous !== name;
  const focusInPanel = document.activeElement?.closest('.tab-panel');
  if (changed && navigation.classList.contains('indicator-ready')) navigation.classList.add('indicator-animated');
  for (const panel of document.querySelectorAll('.tab-panel')) transitions.get(panel)?.cancel();
  document.querySelectorAll('.tab-panel').forEach(el => el.hidden = el.id !== 'tab-' + name);
  document.querySelectorAll('[data-tab]').forEach(el => { el.classList.toggle('active', el.dataset.tab === name); if (el.dataset.tab === name) el.setAttribute('aria-current', 'page'); else el.removeAttribute('aria-current'); });
  positionNavigation();
  $('#page-title').textContent = { print: '新建打印', jobs: state.me?.role === 'admin' ? '打印记录' : '我的打印记录', admin: '系统管理' }[name];
  $('#page-description').textContent = { print: '先选文件、看看预览，再设置打印机和份数。', jobs: state.me?.role === 'admin' ? '查看每次打印的进度和页数，也可以登记实际打印结果。' : '查看每次打印的进度和页数。', admin: '看看打印服务是否正常，设置共享打印机和使用账号。' }[name];
  closeAccountMenu();
  if (changed) {
    window.scrollTo({ top: 0, behavior: 'instant' });
    if (focusInPanel) navigation.querySelector('button.active').focus({ preventScroll: true });
    reveal($('#tab-' + name));
    reveal($('.page-header'), 4);
  }
  if (name === 'jobs') loadJobs().catch(e => toast(e.message, true));
  if (name === 'admin') loadAdmin().catch(e => toast(e.message, true));
}
document.querySelectorAll('[data-tab]').forEach(el => el.addEventListener('click', () => tab(el.dataset.tab)));
$('#empty-new-print').addEventListener('click', () => tab('print'));
$('#mobile-account').addEventListener('click', () => { const open = $('#account-menu').classList.toggle('is-open'); $('#mobile-account').setAttribute('aria-expanded', String(open)); });
document.addEventListener('click', e => { if (!$('#account-menu').contains(e.target) && !$('#mobile-account').contains(e.target)) closeAccountMenu(); });
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeAccountMenu(); });
$('#login-form').addEventListener('submit', async e => {
  e.preventDefault(); const b = e.target.querySelector('button'); b.disabled = true; $('#login-error').textContent = '';
  try { const data = Object.fromEntries(new FormData(e.target)); await enter(await api('/login', 'POST', data)); e.target.reset(); }
  catch (err) { $('#login-error').textContent = err.message; } finally { b.disabled = false; }
});
$('#logout').addEventListener('click', async () => { try { await api('/logout', 'POST'); showLogin(); } catch (e) { toast(e.message, true); } });
async function loadPrinters() {
  const select = $('#printer'), previous = select.value; select.replaceChildren(text('option', '请选择打印机')); select.firstChild.value = '';
  try {
    state.printers = await api('/printers'); const enabled = state.printers.filter(p => p.enabled);
    for (const p of enabled) { const option = text('option', p.name); option.value = p.name; select.append(option); }
    select.value = enabled.some(p => p.name === previous) ? previous : enabled.length === 1 ? enabled[0].name : '';
    $('#printer-message').textContent = enabled.length ? '文件会发到这台打印机，请确认选对了。' : '还没有可用的打印机，请让管理员在“系统管理”中开启共享。';
  } catch (err) { state.printers = []; $('#printer-message').textContent = err.message; }
  updateEstimate();
}
async function upload(file) {
  if (!file || state.uploading || state.submitting) return;
  state.uploading = true; state.doc = null; state.requestId = null; $('.upload-card').setAttribute('aria-busy', 'true');
  $('#file-info').hidden = true; $('#preview').hidden = true; $('#preview').removeAttribute('src'); $('#preview-empty').hidden = false;
  $('#upload-progress').textContent = '正在上传文件、准备预览，稍等一下…'; updateEstimate();
  try {
    const data = new FormData(); data.append('file', file); const doc = await api('/documents', 'POST', data);
    state.doc = doc; state.requestId = crypto.randomUUID ? crypto.randomUUID() : uuid();
    $('#file-name').textContent = doc.name; $('#file-meta').textContent = `${doc.pages} 页 · ${(doc.size / 1024 / 1024).toFixed(2)} MB · SHA-256 ${doc.hash.slice(0, 12)}…`;
    $('#file-info').hidden = false; const url = `/api/documents/${doc.id}/pdf`;
    $('#preview-link').href = url; $('#preview').src = url; $('#preview').hidden = false; $('#preview-empty').hidden = true;
    $('#pages').value = ''; $('#confirmed').checked = false; $('#upload-progress').textContent = '预览出来了，先看看内容和页码有没有问题。';
  } catch (err) { $('#upload-progress').textContent = '上传失败：' + err.message; toast(err.message, true); }
  finally { state.uploading = false; $('.upload-card').setAttribute('aria-busy', 'false'); updateEstimate(); }
}
function uuid() { return '10000000-1000-4000-8000-100000000000'.replace(/[018]/g, c => (Number(c) ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> Number(c) / 4).toString(16)); }
$('#file').addEventListener('change', e => upload(e.target.files[0]));
for (const name of ['dragenter', 'dragover']) $('#dropzone').addEventListener(name, e => { e.preventDefault(); $('#dropzone').classList.add('dragging'); });
for (const name of ['dragleave', 'drop']) $('#dropzone').addEventListener(name, e => { e.preventDefault(); $('#dropzone').classList.remove('dragging'); });
$('#dropzone').addEventListener('drop', e => upload(e.dataTransfer.files[0]));
function selectedCount() {
  if (!state.doc) return 0; const value = $('#pages').value.trim(); if (!value) return state.doc.pages;
  const result = new Set();
  for (const item of value.replaceAll('，', ',').split(',')) {
    if (!/^\s*\d+\s*(-\s*\d+\s*)?$/.test(item)) throw new Error('页码请这样填：1-3,5（表示第 1、2、3、5 页）。');
    const [a, rawB] = item.split('-').map(x => Number(x.trim())); const b = rawB ?? a;
    if (a < 1 || b < a || b > state.doc.pages) throw new Error(`这份文件共 ${state.doc.pages} 页，请检查页码是否填对。`);
    for (let n = a; n <= b; n++) result.add(n);
  } return result.size;
}
function updateEstimate() {
  let valid = false;
  try {
    const n = selectedCount(), copies = Number($('#copies').value);
    valid = n > 0 && Number.isInteger(copies) && copies >= 1 && copies <= 100;
    $('#estimate-pages').textContent = valid ? `${n * copies} 页` : state.doc ? '请检查打印设置' : '还没选择文件';
    $('#estimate-sheets').textContent = valid ? `预计用 ${($('#duplex').value === 'simplex' ? n : Math.ceil(n / 2)) * copies} 张纸 · ${copies} 份` : '选好文件后，这里会显示预计用纸量';
  } catch (e) { $('#estimate-pages').textContent = '页码有误'; $('#estimate-sheets').textContent = e.message; }
  $('#submit-print').disabled = !valid || !$('#printer').value || state.uploading || state.submitting;
  $('#estimate-pages').classList.toggle('is-empty', !valid);
  $('#submit-print').firstChild.textContent = state.submitting ? '正在发送…' : '开始打印';
  $('#submit-help').textContent = state.uploading ? '预览还在准备中，稍等一下。' : !state.doc ? '先选一份文件，再选择打印机。' : !valid ? '请检查要打印的页码和份数。' : !$('#printer').value ? '还需要选一台打印机。' : '确认没有问题后，就可以开始打印了。';
  $('#print-form').setAttribute('aria-busy', String(state.submitting));
}
$('#print-form').addEventListener('input', updateEstimate);
$('#print-form').addEventListener('submit', async e => {
  e.preventDefault(); if (!state.doc || state.submitting) return;
  state.submitting = true; updateEstimate();
  try {
    await api('/jobs', 'POST', { documentId: state.doc.id, printer: $('#printer').value, pages: $('#pages').value.trim(), copies: Number($('#copies').value), duplex: $('#duplex').value, color: $('#color').value === 'true', requestId: state.requestId });
    toast('已经排好队了，可以在打印记录里查看进度。'); state.doc = null; state.requestId = null; $('#confirmed').checked = false; tab('jobs');
  } catch (err) { toast(err.message, true); } finally { state.submitting = false; updateEstimate(); }
});
async function loadJobs() { state.jobs = await api('/jobs'); renderJobs(); }
function renderJobs() {
  const term = $('#search').value.toLowerCase(), status = $('#status-filter').value, from = $('#date-from').value, to = $('#date-to').value;
  const rows = state.jobs.filter(j => {
    const day = new Date(j.created); const localDay = `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, '0')}-${String(day.getDate()).padStart(2, '0')}`;
    return `${j.name} ${j.owner} ${j.printer}`.toLowerCase().includes(term) && (!status || j.status === status) && (!from || localDay >= from) && (!to || localDay <= to);
  });
  $('#metric-jobs').textContent = rows.length; $('#metric-pages').textContent = rows.filter(j => !['failed', 'cancelled'].includes(j.status)).reduce((n, j) => n + j.plannedPages, 0);
  $('#metric-actual').textContent = rows.reduce((n, j) => n + (j.actualPages ?? 0), 0); $('#metric-pending').textContent = rows.filter(j => ['unknown', 'submitted_unverified'].includes(j.status)).length;
  const body = $('#job-rows'); body.replaceChildren(); $('#jobs-empty').hidden = !!rows.length;
  $('#job-count').textContent = `共 ${rows.length.toLocaleString('zh-CN')} 条记录`;
  $('#jobs-empty strong').textContent = state.jobs.length ? '没有找到符合条件的记录' : '还没有打印记录';
  $('#jobs-empty p').textContent = state.jobs.length ? '试试换个关键词，或放宽状态和日期的限制。' : '打印后，可以在这里查看进度和页数。';
  for (const j of rows) {
    const tr = document.createElement('tr'); const a = document.createElement('td'); a.append(text('strong', j.name), text('small', `${j.owner} · ${j.id.slice(0, 8)}`));
    const b = document.createElement('td'); b.append(text('strong', j.printer), text('small', date(j.created)));
    const c = document.createElement('td'); c.className = 'num'; c.append(text('strong', `${j.plannedPages} 页 / ${j.estimatedSheets} 张纸`), text('small', `共 ${j.documentPages} 页，选 ${j.selectedPages} 页 × ${j.copies} 份 · ${j.duplex === 'simplex' ? '单面' : '双面'} · ${j.color ? '彩色' : '黑白'}`));
    const d = document.createElement('td'); d.append(text('span', statuses[j.status] || j.status, 'badge ' + j.status)); if (j.note) { const note = text('small', j.note); d.append(note); }
    const count = text('td', j.actualPages === null ? '还未确认' : `${j.actualPages} 页（已核对）`, 'num');
    const actions = document.createElement('td'), group = text('div', '', 'row-actions'); const preview = text('a', '文件预览', 'button'); preview.href = `/api/documents/${j.documentId}/pdf`; preview.target = '_blank'; preview.rel = 'noopener'; group.append(preview);
    if (j.status === 'queued') { const cancel = text('button', '取消'); cancel.addEventListener('click', async () => { try { await api(`/jobs/${j.id}/cancel`, 'POST'); await loadJobs(); } catch (e) { toast(e.message, true); } }); group.append(cancel); }
    if (state.me.role === 'admin' && ['submitted_unverified', 'unknown', 'failed', 'reviewed'].includes(j.status)) { const review = text('button', '登记结果'); review.addEventListener('click', () => { state.reviewId = j.id; $('#review-form').reset(); $('#review-form').elements.actualPages.value = j.actualPages ?? ''; $('#review-dialog').showModal(); }); group.append(review); }
    actions.append(group); tr.append(a, b, c, d, count, actions); body.append(tr);
  }
}
for (const s of ['#search', '#status-filter', '#date-from', '#date-to']) $(s).addEventListener('input', renderJobs);
$('#refresh-jobs').addEventListener('click', () => loadJobs().catch(e => toast(e.message, true)));
let converterSaving = false;
const converterName = provider => provider === 'microsoft-office' ? 'Microsoft Office' : 'LibreOffice';
function showConversion(info) {
  if (converterSaving) return;
  $('#office-converter').value = info.provider;
  $('#office-converter').disabled = false; $('#converter-save').disabled = false;
  $('#converter-note').hidden = info.provider !== 'microsoft-office';
  $('#converter-status').textContent = `当前使用 ${converterName(info.provider)}。`;
  const components = $('#converter-components'); components.replaceChildren();
  for (const [name, found] of [['LibreOffice', info.libreOffice], ['Microsoft Word', info.microsoftWord], ['Microsoft Excel', info.microsoftExcel]])
    components.append(text('span', `${found ? '●' : '○'} ${name}：${found ? '已检测到' : '没找到'}`, found ? 'good' : 'bad'));
}
$('#office-converter').addEventListener('change', () => {
  $('#converter-note').hidden = $('#office-converter').value !== 'microsoft-office';
  $('#converter-status').textContent = '选好后点击“保存选择”，新上传的 Word / Excel 就会使用它。';
});
$('#converter-form').addEventListener('submit', async e => {
  e.preventDefault(); if (converterSaving) return;
  converterSaving = true; $('#converter-form').setAttribute('aria-busy', 'true');
  $('#converter-save').disabled = true; $('#office-converter').disabled = true;
  $('#converter-status').textContent = '正在保存…';
  try {
    const info = await api('/conversion', 'POST', { provider: $('#office-converter').value });
    converterSaving = false; showConversion(info);
    const ready = info.provider === 'microsoft-office' ? info.microsoftWord && info.microsoftExcel : info.libreOffice;
    $('#converter-status').textContent = ready ? `已保存，新上传的 Word / Excel 会使用 ${converterName(info.provider)}。` : '选择已保存，但有转换软件没找到。请先在服务电脑安装，已有的 PDF 和图片仍可正常使用。';
    toast('文档转换设置已保存。');
  } catch (err) { $('#converter-status').textContent = err.message; toast(err.message, true); }
  finally { converterSaving = false; $('#converter-form').setAttribute('aria-busy', 'false'); $('#converter-save').disabled = false; $('#office-converter').disabled = false; }
});
async function loadAdmin() {
  const results = await Promise.allSettled([api('/system'), api('/users'), api('/events'), loadPrinters()]);
  for (const r of results) if (r.status === 'rejected') toast(r.reason.message, true);
  if (results[0].status === 'fulfilled') {
    const sys = results[0].value; $('#diagnostics').replaceChildren(); $('#retention').textContent = sys.retentionDays;
    for (const [label, ok, yes, no] of [['Windows 打印支持', sys.windows, '可用', '当前系统不支持'], ['PDF 打印组件', sys.sumatra, '可用', '没找到 SumatraPDF'], ['实机打印', sys.printingEnabled, '已开启', '已关闭']]) $('#diagnostics').append(text('span', `${ok ? '●' : '○'} ${label}：${ok ? yes : no}`, ok ? 'good' : 'bad'));
    showConversion(sys.conversion);
    if (sys.printerError) $('#diagnostics').append(text('p', sys.printerError, 'error'));
  } else $('#converter-status').textContent = '暂时没能读取转换设置，请点击“刷新状态”再试一次。';
  $('#printer-list').replaceChildren();
  if (!state.printers.length) $('#printer-list').append(text('p', '还没找到可共享的打印机。请先在服务电脑上安装驱动，确认能正常打印后，再刷新看看。', 'muted'));
  for (const p of state.printers) {
    const row = text('div', '', 'printer-item'), info = document.createElement('div'); info.append(text('strong', p.name), text('small', `${p.driver} · ${p.status}`));
    const check = document.createElement('input'); check.type = 'checkbox'; check.checked = p.enabled; check.setAttribute('aria-label', '共享 ' + p.name);
    check.addEventListener('change', async () => { check.disabled = true; try { await api('/printers', 'POST', { name: p.name, enabled: check.checked }); await loadPrinters(); toast('打印机共享设置已保存。'); } catch (e) { check.checked = !check.checked; toast(e.message, true); } finally { check.disabled = false; } }); row.append(info, check); $('#printer-list').append(row);
  }
  if (results[1].status === 'fulfilled') { $('#user-list').replaceChildren(); for (const u of results[1].value) $('#user-list').append(text('span', `${u.name} · ${u.role === 'admin' ? '管理员' : '用户'}`)); }
  if (results[2].status === 'fulfilled') { $('#event-rows').replaceChildren(); for (const ev of results[2].value) { const tr = document.createElement('tr'); for (const value of [date(ev.created), ev.actor, eventNames[ev.action] || ev.action, ev.detail]) tr.append(text('td', value)); $('#event-rows').append(tr); } }
}
$('#refresh-admin').addEventListener('click', () => loadAdmin().catch(e => toast(e.message, true)));
$('#user-form').addEventListener('submit', async e => { e.preventDefault(); try { await api('/users', 'POST', Object.fromEntries(new FormData(e.target))); e.target.reset(); toast('账户已创建。'); await loadAdmin(); } catch (err) { toast(err.message, true); } });
$('#password-open').addEventListener('click', () => { closeAccountMenu(); $('#password-form').reset(); $('#password-dialog').showModal(); });
$('#password-close').addEventListener('click', () => $('#password-dialog').close());
$('#review-close').addEventListener('click', () => $('#review-dialog').close());
$('#password-form').addEventListener('submit', async e => { e.preventDefault(); try { await api('/password', 'POST', Object.fromEntries(new FormData(e.target))); $('#password-dialog').close(); toast('密码已更新。'); } catch (err) { toast(err.message, true); } });
$('#review-form').addEventListener('submit', async e => { e.preventDefault(); const data = new FormData(e.target); try { await api(`/jobs/${state.reviewId}/review`, 'POST', { actualPages: data.get('actualPages') === '' ? null : Number(data.get('actualPages')), note: data.get('note') }); $('#review-dialog').close(); await loadJobs(); toast('打印结果已保存。'); } catch (err) { toast(err.message, true); } });
setInterval(() => { if (state.me && !$('#tab-jobs').hidden && !document.hidden) loadJobs().catch(() => {}); }, 8000);
api('/me').then(enter).catch(() => showLogin());
