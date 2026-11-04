import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, rm, readFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function freePort() {
  const socket = net.createServer();
  await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
  const port = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  return port === 17821 ? freePort() : port;
}

test('real HTTP server smoke (isolated data, no TIA/MCP)', { timeout: 30000 }, async t => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'tai-http-smoke-'));
  const port = await freePort();
  const base = `http://127.0.0.1:${port}`;
  let output = '';
  let child;
  let mock;
  t.after(async () => {
    if (child && child.exitCode === null && child.signalCode === null) {
      const exited = once(child, 'exit');
      child.kill();
      const force = setTimeout(() => child.kill('SIGKILL'), 2500);
      await exited;
      clearTimeout(force);
    }
    if (mock) { mock.closeAllConnections(); await new Promise(resolve => mock.close(resolve)); }
    await rm(directory, { recursive: true, force: true });
  });
  child = spawn(process.execPath, ['server/index.mjs'], {
    cwd: root, env: { ...process.env, TAI_PORT: String(port), TAI_DATA_DIR: directory },
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout.on('data', chunk => { output += chunk; });
  child.stderr.on('data', chunk => { output += chunk; });
  const startupError = new Promise((_, reject) => child.once('error', reject));
  await Promise.race([startupError, (async () => {
    for (let i = 0; i < 100; i++) {
      if (output.includes('TAI ready:')) return;
      if (child.exitCode !== null) throw new Error(`Server exited: ${output}`);
      await delay(50);
    }
    throw new Error(`Server startup timeout: ${output}`);
  })()]);
  async function request(route, method = 'GET', data, headers = {}) {
    const res = await fetch(base + route, {
      method, headers: { ...(data === undefined ? {} : { 'Content-Type': 'application/json' }), ...headers },
      body: data === undefined ? undefined : JSON.stringify(data), signal: AbortSignal.timeout(5000),
    });
    const text = await res.text();
    return { status: res.status, headers: res.headers, text, json: () => JSON.parse(text) };
  }
  let conversation;
  const secret = 'fake-smoke-key-not-a-user-secret';
  await t.test('status starts idle with no MCP and no model', async () => {
    const r = await request('/api/status');
    assert.equal(r.status, 200);
    assert.deepEqual(r.json().mcp, { running: false, connected: false, toolCount: 0 });
    assert.equal(r.json().busy, false);
    assert.equal(r.json().model.configured, false);
  });
  await t.test('configuration never returns API key; preserve and clear semantics', async () => {
    for (const input of [{ apiKey: secret }, { apiKey: '' }, { temperature: 0.5 }]) {
      const r = await request('/api/config', 'PUT', input);
      assert.equal(r.status, 200);
      assert.equal(r.json().hasApiKey, true);
      assert.equal(Object.hasOwn(r.json(), 'apiKey'), false);
      assert.equal(r.text.includes(secret), false);
    }
    const r = await request('/api/config');
    assert.equal(r.text.includes(secret), false);
    assert.equal(r.headers.get('cache-control'), 'no-store');
    assert.equal(JSON.parse(await readFile(path.join(directory, 'config.json'), 'utf8')).apiKey, secret);
    assert.equal((await request('/api/config', 'PUT', { clearApiKey: true })).json().hasApiKey, false);
    assert.equal((await request('/api/config', 'PUT', { maxSteps: 0 })).json().maxSteps, 0);
  });
  await t.test('prompt edit save reset and validation persist without touching secrets', async () => {
    const initial=(await request('/api/prompts')).json();
    assert.ok(initial.defaultSystemPrompt.length>100);
    const prompt='工程规范\n使用中文，保留变量名称。';
    assert.equal((await request('/api/prompts','PUT',{systemPrompt:prompt})).json().systemPrompt,prompt);
    assert.equal(JSON.parse(await readFile(path.join(directory,'config.json'),'utf8')).systemPrompt,prompt);
    assert.equal((await request('/api/prompts')).json().systemPrompt,prompt);
    assert.equal((await request('/api/prompts','PUT',{systemPrompt:123})).status,400);
    assert.equal((await request('/api/prompts','PUT',{systemPrompt:'x'.repeat(100001)})).status,400);
    assert.equal((await request('/api/prompts','PUT',{systemPrompt:''})).json().systemPrompt,'');
  });
  await t.test('Host, Origin, fetch-site and content-type protections', async () => {
    const rejectedHost = await new Promise((resolve, reject) => {
      const req = http.get(base + '/api/status', { headers: { Host: 'attacker.invalid' } }, res => {
        res.resume();
        res.on('end', () => resolve(res.statusCode));
      });
      req.on('error', reject);
    });
    assert.equal(rejectedHost, 403);
    assert.equal((await request('/api/status', 'GET', undefined, { Origin: 'https://attacker.invalid' })).status, 403);
    assert.equal((await request('/api/status', 'GET', undefined, { Origin: 'null' })).status, 403);
    assert.equal((await request('/api/status', 'GET', undefined, { 'Sec-Fetch-Site': 'cross-site' })).status, 403);
    assert.equal((await request('/api/status', 'GET', undefined, { Origin: base })).status, 200);
    assert.equal((await request('/api/conversations', 'POST', {}, { 'Content-Type': 'text/plain' })).status, 415);
    assert.equal((await request('/api/not-found')).status, 404);
  });
  await t.test('conversation create, list, read and delete persist', async () => {
    assert.deepEqual((await request('/api/conversations')).json().conversations, []);
    const created = await request('/api/conversations', 'POST', { title: 'HTTP smoke' });
    assert.equal(created.status, 201);
    conversation = created.json();
    assert.equal(conversation.title, 'HTTP smoke');
    assert.deepEqual((await request(`/api/conversations/${conversation.id}`)).json(), conversation);
    assert.equal((await request('/api/conversations')).json().conversations.length, 1);
    const other = (await request('/api/conversations', 'POST', {})).json();
    assert.equal((await request(`/api/conversations/${other.id}?revision=${other.revision}`, 'DELETE')).status, 200);
    assert.equal((await request(`/api/conversations/${other.id}`)).status, 400);
    const saved = JSON.parse(await readFile(path.join(directory, 'conversations.json'), 'utf8'));
    assert.deepEqual(saved.map(c => c.id), [conversation.id]);
  });
  await t.test('missing model produces error and done NDJSON, updates conversation', async () => {
    const r = await request('/api/chat', 'POST', { conversationId: conversation.id, message: '你好', mode: 'read' });
    assert.equal(r.status, 200);
    assert.match(r.headers.get('content-type'), /application\/x-ndjson/);
    const events = r.text.trim().split('\n').map(line => JSON.parse(line));
    assert.deepEqual(events.map(e => e.type), ['task', 'status', 'error', 'done']);
    assert.equal(events[0].conversationId, conversation.id);
    assert.ok(events[0].taskId);
    assert.match(events[2].error, /模型名称/);
    assert.equal(events[3].conversationId, conversation.id);
    const saved = (await request(`/api/conversations/${conversation.id}`)).json();
    assert.equal(saved.title, 'HTTP smoke');
    assert.deepEqual(saved.messages.map(m => m.role), ['user', 'assistant']);
    assert.equal((await request('/api/status')).json().busy, false);
    assert.equal((await request('/api/chat', 'POST', { conversationId: conversation.id, message: '', mode: 'read' })).status, 400);
    assert.equal((await request('/api/chat', 'POST', { conversationId: conversation.id, message: 'x', mode: 'invalid' })).status, 400);
    assert.equal((await request('/api/model/test', 'POST', {})).status, 400);
  });
  await t.test('busy excludes config, deletion and second chat; cancel releases task', async () => {
    let received;
    const incoming = new Promise(resolve => { received = resolve; });
    mock = http.createServer((req, res) => {
      req.resume();
      req.on('end', () => { received({ url: req.url, authorization: req.headers.authorization }); });
      // Deliberately hold response until cancellation; never contact an external model.
    });
    await new Promise(resolve => mock.listen(0, '127.0.0.1', resolve));
    const modelPort = mock.address().port;
    assert.equal((await request('/api/config', 'PUT', { baseUrl: `http://127.0.0.1:${modelPort}/v1`, model: 'fake-model', clearApiKey: true })).status, 200);
    const chat = request('/api/chat', 'POST', { conversationId: conversation.id, message: 'hold', mode: 'read' });
    const seen = await Promise.race([incoming, delay(3000).then(() => { throw new Error('Mock model not called'); })]);
    assert.equal(seen.url, '/v1/chat/completions');
    assert.equal(seen.authorization, undefined);
    assert.equal((await request('/api/status')).json().busy, true);
    assert.equal((await request('/api/config', 'PUT', { temperature: 0.2 })).status, 409);
    const runningConversation = (await request(`/api/conversations/${conversation.id}`)).json();
    assert.equal((await request(`/api/conversations/${conversation.id}?revision=${runningConversation.revision}`, 'DELETE')).status, 409);
    assert.equal((await request('/api/chat', 'POST', { conversationId: conversation.id, message: 'second', mode: 'read' })).status, 409);
    assert.equal((await request('/api/chat/cancel', 'POST', {})).status, 200);
    const events = (await chat).text.trim().split('\n').map(line => JSON.parse(line));
    assert.match(events.find(e => e.type === 'error').error, /取消/);
    assert.equal(events.at(-1).type, 'done');
    assert.equal((await request('/api/status')).json().busy, false);
    assert.deepEqual((await request('/api/status')).json().mcp, { running: false, connected: false, toolCount: 0 });
  });
  await t.test('regression: UTF-8 characters split across HTTP chunks remain intact', async () => {
    const payload = Buffer.from(JSON.stringify({ title: '中文标题' }));
    const split = payload.indexOf(Buffer.from('中')) + 1;
    const result = await new Promise((resolve, reject) => {
      const req = http.request(base + '/api/conversations', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
      }, res => {
        let text = '';
        res.setEncoding('utf8');
        res.on('data', chunk => { text += chunk; });
        res.on('end', () => resolve(JSON.parse(text)));
      });
      req.on('error', reject);
      req.write(payload.subarray(0, split));
      setTimeout(() => req.end(payload.subarray(split)), 80);
    });
    assert.equal(result.title, '中文标题');
  });
});
