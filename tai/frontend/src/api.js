import { readNdjsonStream } from './ndjson.js';

export class ApiError extends Error {
  constructor(message, status = 0, data = null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.data = data;
  }
}

const OFFLINE_HINT = '无法连接 TAI 本地服务（127.0.0.1:17821），请确认后端已启动。';

async function parseError(res) {
  let text = '';
  try {
    text = await res.text();
  } catch {
    /* ignore */
  }
  let data = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = null;
    }
  }
  const message =
    (data && typeof data.error === 'string' && data.error) ||
    (res.status === 409 ? '后端正忙：已有对话正在运行。' : '') ||
    `HTTP ${res.status}${res.statusText ? ' ' + res.statusText : ''}`;
  return new ApiError(message, res.status, data);
}

async function request(method, path, body, { signal, timeoutMs } = {}) {
  const init = { method, headers: { Accept: 'application/json' } };
  if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  let timer = null;
  let controller = null;
  if (timeoutMs) {
    controller = new AbortController();
    timer = setTimeout(() => controller.abort(), timeoutMs);
    if (signal) signal.addEventListener('abort', () => controller.abort(), { once: true });
    init.signal = controller.signal;
  } else if (signal) {
    init.signal = signal;
  }
  let res;
  try {
    res = await fetch(path, init);
  } catch (error) {
    if (timer) clearTimeout(timer);
    if (error?.name === 'AbortError') {
      if (signal?.aborted) throw error;
      throw new ApiError('请求超时，本地服务无响应。', 0);
    }
    throw new ApiError(OFFLINE_HINT, 0);
  }
  if (timer) clearTimeout(timer);
  if (!res.ok) throw await parseError(res);
  const text = await res.text();
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    throw new ApiError('服务返回了无法解析的响应。', res.status);
  }
}

const enc = encodeURIComponent;

export const api = {
  status: () => request('GET', '/api/status', undefined, { timeoutMs: 6000 }),
  updates: (refresh = false) => request('GET', '/api/updates' + (refresh ? '?refresh=1' : ''), undefined, { timeoutMs: 12000 }),
  models: (refresh = false) => request('GET', '/api/models' + (refresh ? '?refresh=1' : ''), undefined, { timeoutMs: 20000 }),
  getConfig: () => request('GET', '/api/config'),
  putConfig: (config) => request('PUT', '/api/config', config),
  getPrompts: () => request('GET', '/api/prompts'),
  putPrompts: (systemPrompt) => request('PUT', '/api/prompts', { systemPrompt: String(systemPrompt ?? '') }),
  testModel: () => request('POST', '/api/model/test', {}),
  tiaConnect: () => request('POST', '/api/tia/connect', {}),
  tiaDisconnect: () => request('POST', '/api/tia/disconnect', {}),
  tiaState: () => request('GET', '/api/tia/state'),
  tools: () => request('GET', '/api/tools'),
  callTool: (name, args, projectId) => request('POST', '/api/tools/call', { name, arguments: args ?? {}, ...(projectId ? { projectId } : {}) }),
  projects: () => request('GET', '/api/projects'),
  createProject: (data) => request('POST', '/api/projects', data),
  updateProject: (id, data) => request('PUT', `/api/projects/${enc(id)}`, data),
  conversations: (filters) => request('GET', '/api/conversations' + (filters ? '?' + new URLSearchParams(filters) : '')),
  createConversation: (title, projectId) => request('POST', '/api/conversations', { ...(title ? { title } : {}), ...(projectId ? { projectId } : {}) }),
  updateConversation: (id, data) => request('PUT', `/api/conversations/${enc(id)}`, data),
  conversation: (id) => request('GET', `/api/conversations/${enc(id)}`),
  deleteProject: (id, revision) => request('DELETE', `/api/projects/${enc(id)}?revision=${enc(revision)}`),
  deleteConversation: (id, revision) => request('DELETE', `/api/conversations/${enc(id)}${revision === undefined ? '' : '?revision=' + enc(revision)}`),
  cancelChat: (taskId) => request('POST', '/api/chat/cancel', taskId ? { taskId } : {}),
};

/**
 * POST /api/chat，逐行回调 NDJSON 事件。
 * 抛出 ApiError（HTTP 错误，如 409 忙碌）、AbortError（本地中止）或网络错误。
 */
export async function streamChat({ conversationId, message, mode, model, reasoningEffort }, { signal, onEvent, onInvalid }) {
  let res;
  try {
    res = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/x-ndjson, application/json' },
      body: JSON.stringify({ conversationId, message, mode, ...(model?.trim() ? { model: model.trim() } : {}), ...(reasoningEffort ? { reasoningEffort } : {}) }),
      signal,
    });
  } catch (error) {
    if (error?.name === 'AbortError') throw error;
    throw new ApiError(OFFLINE_HINT, 0);
  }
  if (!res.ok) throw await parseError(res);
  if (!res.body) throw new ApiError('浏览器不支持流式响应。', res.status);
  try {
    await readNdjsonStream(res.body, onEvent, onInvalid);
  } catch (error) {
    if (error?.name === 'AbortError' || signal?.aborted) {
      const abort = new Error('已中止');
      abort.name = 'AbortError';
      throw abort;
    }
    throw new ApiError(`响应流中断：${error?.message || error}`, 0);
  }
}
