import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import path from 'node:path';
import { resolveMcpPath } from './tia-version.mjs';
import { checkedResult } from './results.mjs';

export class McpClient {
  constructor() { this.pending = new Map(); this.sequence = 0; this.tools = []; this.connected = false; this.logs = []; }
  async start(config) {
    if (this.ready) return;
    if (this.starting) return this.starting;
    this.starting = this.initialize(config).finally(() => { this.starting = null; });
    return this.starting;
  }
  async initialize(config) {
    const mcpPath = resolveMcpPath(config);
    const child = spawn(mcpPath, ['--tia-major-version', String(config.tiaVersion), '--logging', '1'], {
      cwd: path.dirname(mcpPath), windowsHide: true,
      env: { ...process.env, TiaPortalLocation: config.tiaLocation }, stdio: ['pipe', 'pipe', 'pipe']
    });
    this.child = child;
    const fail = error => {
      if (this.child !== child) return;
      this.ready = false; this.connected = false; this.child = null; this.tools = [];
      for (const { reject, timer } of this.pending.values()) { clearTimeout(timer); reject(error); }
      this.pending.clear();
      if (!child.killed) child.kill();
    };
    child.on('error', fail);
    child.stdin.on('error', fail);
    child.stdout.on('error', fail);
    child.stderr.on('error', fail);
    child.on('exit', code => fail(new Error(`TIA MCP 已退出 (${code})。请检查安装路径和 Siemens TIA Openness 用户组。`)));
    createInterface({ input: child.stderr }).on('line', line => { this.logs.push(line); if (this.logs.length > 80) this.logs.shift(); });
    createInterface({ input: child.stdout }).on('line', line => {
      let msg; try { msg = JSON.parse(line); } catch { return; }
      if (msg.method && msg.id !== undefined) {
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: msg.id, error: { code: -32601, message: 'Unsupported client method' } }) + '\n'); return;
      }
      const entry = this.pending.get(msg.id); if (!entry) return;
      this.pending.delete(msg.id); clearTimeout(entry.timer);
      if (msg.error) entry.reject(new Error(msg.error.message)); else entry.resolve(msg.result);
    });
    try {
      await this.request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'TAI', version: '0.1.0' } }, 90000);
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
      let cursor; const tools = [];
      do { const result = await this.request('tools/list', cursor ? { cursor } : {}); tools.push(...result.tools); cursor = result.nextCursor; } while (cursor);
      this.tools = tools; this.ready = true;
    } catch (error) { this.stop(); throw error; }
  }
  request(method, params, timeout = 240000) {
    return new Promise((resolve, reject) => {
      if (!this.child?.stdin.writable) return reject(new Error('TIA MCP 未启动'));
      const id = ++this.sequence;
      const timer = setTimeout(() => {
        this.pending.delete(id); reject(new Error(`${method} 超时；本次连接已关闭，请重新连接并核对工程状态。`)); this.stop();
      }, timeout);
      this.pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n', error => {
        if (error) { clearTimeout(timer); this.pending.delete(id); reject(error); }
      });
    });
  }
  async call(name, args = {}) {
    const result = checkedResult(await this.request('tools/call', { name, arguments: args }));
    if (!result.isError && name === 'Connect') this.connected = true;
    if (!result.isError && name === 'Disconnect') this.connected = false;
    if (name === 'GetState' && !result.isError) {
      try { const state = result.structuredContent ?? JSON.parse(result.content.find(c => c.type === 'text').text); this.connected = Boolean(state.IsConnected ?? state.isConnected); } catch {}
    }
    return result;
  }
  stop() {
    const child = this.child; this.child = null; this.ready = false; this.connected = false; this.tools = [];
    for (const item of this.pending.values()) { clearTimeout(item.timer); item.reject(new Error('MCP 连接已关闭')); }
    this.pending.clear(); child?.kill();
  }
}
