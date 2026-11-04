// Read-only smoke of the actual installed UI. No conversation creation, model call or engineering action.
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
const base = process.env.TAI_TEST_URL || 'http://127.0.0.1:17821';
const expectedVersion = process.argv[2] || '0.11.0';
const status = await (await fetch(base + '/api/status')).json();
assert.equal(status.app, 'TAI');
assert.equal(status.version, expectedVersion);
const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'tai-installed-smoke-'));
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', ['--headless=new', '--disable-gpu', '--no-first-run', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' });
let ws;
try {
  let port;
  for (let i = 0; i < 150; i++) { try { port = (await fs.readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; break; } catch { await new Promise(r => setTimeout(r, 100)); } }
  assert.ok(port, 'Temporary browser ready');
  const target = await (await fetch(`http://127.0.0.1:${port}/json/new?${base}/workspace?project=default`, { method: 'PUT' })).json();
  ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  let sequence = 0; const pending = new Map(), errors = [];
  ws.onmessage = event => { const message = JSON.parse(event.data); if (message.id) { const job = pending.get(message.id); if (job) { pending.delete(message.id); message.error ? job.reject(message.error) : job.resolve(message.result); } } else if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.text); };
  const call = (method, params = {}) => new Promise((resolve, reject) => { const id = ++sequence; pending.set(id, { resolve, reject }); ws.send(JSON.stringify({ id, method, params })); });
  const evaluate = async expression => { const result = await call('Runtime.evaluate', { expression, returnByValue: true }); if (result.exceptionDetails) throw new Error(result.exceptionDetails.text); return result.result.value; };
  const wait = async expression => { for (let i = 0; i < 100; i++) { if (await evaluate(expression)) return; await new Promise(r => setTimeout(r, 100)); } throw new Error('Installed UI expected control missing'); };
  await call('Runtime.enable');
  await call('Emulation.setDeviceMetricsOverride', { width: 1280, height: 820, deviceScaleFactor: 1, mobile: false });
  await wait('Boolean(document.querySelector(".wk-home textarea[aria-label=对话任务]")) || Boolean(document.querySelector(".wk-home") && document.querySelector("textarea[aria-label=对话任务]"))');
  await wait('Boolean(document.querySelector("input[aria-label=对话模型]")) && Boolean(document.querySelector("select[aria-label=思考程度]"))');
  assert.equal(await evaluate('[...document.querySelectorAll("[role=radio]")].map(e=>e.textContent.trim()).join("|")'), '只读|工作区内修改|完全权限');
  assert.equal(await evaluate('document.documentElement.scrollWidth > innerWidth'), false);
  await call('Emulation.setDeviceMetricsOverride', { width: 364, height: 480, deviceScaleFactor: 1, mobile: false });
  await call('Page.navigate', { url: base + '/assistant?project=default' });
  await wait('Boolean(document.querySelector(".wk-compact textarea[aria-label=对话任务]"))');
  assert.equal(await evaluate('document.documentElement.scrollWidth > innerWidth'), false);
  await call('Page.navigate', { url: base + '/settings' });
  await wait('Boolean(document.querySelector("select[aria-label=默认思考程度]"))');
  assert.equal(errors.length, 0, JSON.stringify(errors));
  console.log(`PASS: installed TAI ${expectedVersion}, workspace permission/model/thinking controls, compact 364px and settings; no model requests or project actions.`);
  await call('Browser.close').catch(() => {});
} finally {
  ws?.close(); browser.kill();
  await new Promise(r => setTimeout(r, 500));
  await fs.rm(profile, { recursive: true, force: true, maxRetries: 3 }).catch(() => {});
}
