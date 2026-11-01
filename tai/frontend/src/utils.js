export function cx(...names) {
  return names.filter(Boolean).join(' ');
}

let counter = 0;
export function uid() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  counter += 1;
  return `id-${Date.now().toString(36)}-${counter}-${Math.random().toString(36).slice(2, 8)}`;
}

export async function copyText(text) {
  const value = String(text ?? '');
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(value);
      return true;
    }
  } catch {
    /* 回退 */
  }
  const ta = document.createElement('textarea');
  ta.value = value;
  ta.setAttribute('readonly', '');
  ta.style.position = 'fixed';
  ta.style.top = '-1000px';
  ta.style.opacity = '0';
  document.body.appendChild(ta);
  ta.select();
  let ok = false;
  try {
    ok = document.execCommand('copy');
  } catch {
    ok = false;
  }
  ta.remove();
  return ok;
}

export function prettyJson(value) {
  if (value === undefined) return '';
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

export function toDate(value) {
  if (value === null || value === undefined || value === '') return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

const pad = (n) => String(n).padStart(2, '0');

export function formatTime(value) {
  const d = toDate(value);
  if (!d) return '';
  const now = new Date();
  const hm = `${pad(d.getHours())}:${pad(d.getMinutes())}`;
  if (d.toDateString() === now.toDateString()) return hm;
  if (d.getFullYear() === now.getFullYear()) return `${d.getMonth() + 1}月${d.getDate()}日 ${hm}`;
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export function formatFullTime(value) {
  const d = toDate(value);
  if (!d) return '';
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

export function dateGroup(value) {
  const d = toDate(value);
  if (!d) return '更早';
  const start = new Date();
  start.setHours(0, 0, 0, 0);
  const diffDays = Math.floor((start.getTime() - d.getTime()) / 86400000) + 1;
  if (d >= start) return '今天';
  if (diffDays <= 1) return '昨天';
  if (diffDays <= 7) return '近 7 天';
  if (diffDays <= 30) return '近 30 天';
  return '更早';
}

export function makeTitle(text) {
  const t = String(text).replace(/\s+/g, ' ').trim();
  return t.length > 30 ? t.slice(0, 30) + '…' : t;
}

export function fmtDuration(ms) {
  if (!Number.isFinite(ms) || ms < 0) return '';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.floor(ms / 60000)} 分 ${Math.round((ms % 60000) / 1000)} 秒`;
}

function shortValue(v) {
  if (v === null) return 'null';
  if (typeof v === 'string') return v.length > 28 ? `"${v.slice(0, 28)}…"` : `"${v}"`;
  if (Array.isArray(v)) return `[${v.length}]`;
  if (typeof v === 'object') return '{…}';
  return String(v);
}

export function summarizeArgs(args) {
  if (!args || typeof args !== 'object') return '';
  const entries = Object.entries(args);
  if (!entries.length) return '';
  const s = entries
    .slice(0, 3)
    .map(([k, v]) => `${k}=${shortValue(v)}`)
    .join(', ');
  return entries.length > 3 ? `${s}, …` : s;
}

export function providerLabel(p) {
  if (p === 'anthropic') return 'Anthropic';
  if (p === 'openai') return 'OpenAI 兼容';
  return p || '未知';
}

// 与 server/agent.mjs 的 readNames / writeNames / permitted() 对应。
export const MODE_INFO = {
  read: {
    label: '只读',
    short: '仅检查工程',
    desc: '只开放检查类工具（状态、工程、设备、软件树、块与类型信息）和读取 TAI 工件。不会修改博途工程。',
  },
  write: {
    label: '写入',
    short: '允许修改工程',
    desc: '额外允许编译、保存/另存工程、导出与导入块/类型/文档，以及写入 TAI 工件文件。导入会改变当前工程，TAI 无法自动撤销。不包含下载到 PLC、启停 CPU 或强制操作。',
  },
};

export const EFFECT_INFO = {
  read: { label: '只读', tone: 'ok' },
  write: { label: '写入', tone: 'warn' },
  lifecycle: { label: '连接', tone: 'info' },
};

export function normalizePersistedTool(t) {
  if (!t || typeof t !== 'object') return { name: String(t ?? '工具'), state: 'done', extra: t };
  const result = t.result !== undefined ? t.result : t.output;
  return {
    id: t.id,
    name: t.name ?? t.tool ?? t.toolName ?? '工具',
    arguments: t.arguments ?? t.args ?? t.input ?? t.params,
    result,
    isError: Boolean(t.isError ?? (result && typeof result === 'object' && result.isError)),
    state: 'done',
    extra: t,
  };
}

export function findTool(tools, name) {
  if (!Array.isArray(tools)) return null;
  const n = name.toLowerCase();
  return (
    tools.find((t) => String(t.name).toLowerCase() === n) ||
    tools.find((t) => String(t.name).toLowerCase().endsWith(n)) ||
    tools.find((t) => String(t.name).toLowerCase().includes(n)) ||
    null
  );
}
