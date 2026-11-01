// 系统提示词编辑：与 GET/PUT /api/prompts 的约定保持一致的纯函数，便于测试。
//   GET  /api/prompts -> { systemPrompt, defaultSystemPrompt }（systemPrompt 为空表示使用内置默认）
//   PUT  /api/prompts { systemPrompt } -> 同上；空字符串表示恢复内置默认；最多 100000 字符。

export const MAX_PROMPT_CHARS = 100000;
/** 导入文件的字节上限（UTF-8 下 100000 字符最多约 400KB，再给 JSON 包装留余量）。 */
export const MAX_IMPORT_BYTES = 1024 * 1024;
export const IMPORT_ACCEPT = '.txt,.md,.markdown,.json,text/plain,text/markdown,application/json';

/** 规范化服务端响应。 */
export function normalizePrompts(data) {
  return {
    systemPrompt: typeof data?.systemPrompt === 'string' ? data.systemPrompt : '',
    defaultSystemPrompt: typeof data?.defaultSystemPrompt === 'string' ? data.defaultSystemPrompt : '',
  };
}

/** 是否存在用户覆盖（否则使用内置默认）。 */
export function isOverride(prompts) {
  return Boolean(normalizePrompts(prompts).systemPrompt.trim());
}

/** 编辑器初始内容：有覆盖时为覆盖内容，否则载入内置默认。 */
export function effectivePrompt(prompts) {
  const p = normalizePrompts(prompts);
  return p.systemPrompt.trim() ? p.systemPrompt : p.defaultSystemPrompt;
}

export function normalizeNewlines(text) {
  return String(text ?? '').replace(/\r\n?/g, '\n');
}

/** 草稿字段错误；无错误返回空字符串。 */
export function validatePromptDraft(draft) {
  const n = String(draft ?? '').length;
  if (n > MAX_PROMPT_CHARS) return `提示词超出上限：${n} / ${MAX_PROMPT_CHARS} 字符`;
  return '';
}

/**
 * 草稿 → PUT 请求体。与内置默认完全相同或为空白时发送空字符串（不保存覆盖，跟随内置默认）。
 */
export function buildPromptBody(draft, defaultSystemPrompt = '') {
  const text = String(draft ?? '');
  if (!text.trim() || normalizeNewlines(text) === normalizeNewlines(defaultSystemPrompt)) return { systemPrompt: '' };
  return { systemPrompt: text };
}

/** 草稿是否不同于已保存（生效）内容。 */
export function isPromptDirty(draft, prompts) {
  return normalizeNewlines(draft) !== normalizeNewlines(effectivePrompt(prompts));
}

/** 严格 UTF-8 解码（去除 BOM）；非 UTF-8 内容抛出错误。 */
export function decodeUtf8(bytes) {
  const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let text;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(view);
  } catch {
    throw new Error('文件不是有效的 UTF-8 文本，请先另存为 UTF-8 编码');
  }
  return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
}

function extensionOf(name) {
  const m = String(name ?? '').toLowerCase().match(/\.([a-z0-9]+)$/);
  return m ? m[1] : '';
}

/**
 * 解析导入文件内容。.txt/.md 作为纯文本；.json 必须是 { "systemPrompt": string }。
 * @returns {{ systemPrompt: string }} 抛出 Error（中文说明）表示无法导入。
 */
export function parsePromptImport(fileName, text) {
  const ext = extensionOf(fileName);
  let prompt;
  if (ext === 'json') {
    let data;
    try {
      data = JSON.parse(String(text ?? ''));
    } catch (e) {
      throw new Error(`JSON 解析失败：${e.message}`);
    }
    if (!data || typeof data !== 'object' || Array.isArray(data) || typeof data.systemPrompt !== 'string') {
      throw new Error('JSON 文件必须是 {"systemPrompt": "..."} 格式');
    }
    prompt = data.systemPrompt;
  } else if (['txt', 'md', 'markdown'].includes(ext)) {
    prompt = String(text ?? '');
  } else {
    throw new Error('仅支持导入 .txt、.md 或 .json 文件');
  }
  prompt = normalizeNewlines(prompt);
  if (!prompt.trim()) throw new Error('导入的提示词为空；如需恢复内置默认，请使用“恢复内置默认”');
  const err = validatePromptDraft(prompt);
  if (err) throw new Error(err);
  return { systemPrompt: prompt };
}

/** 读取浏览器 File 对象并解析（大小、编码、格式均校验）。 */
export async function readPromptFile(file) {
  if (!file) throw new Error('未选择文件');
  if (file.size > MAX_IMPORT_BYTES) throw new Error(`文件过大（${file.size} 字节），上限 ${MAX_IMPORT_BYTES} 字节`);
  const bytes = new Uint8Array(await file.arrayBuffer());
  return parsePromptImport(file.name, decodeUtf8(bytes));
}

/** 导出内容：format 为 'txt' 或 'json'。 */
export function exportPayload(draft, format = 'txt') {
  const text = String(draft ?? '');
  if (format === 'json') {
    return { fileName: 'tai-system-prompt.json', mime: 'application/json', content: JSON.stringify({ systemPrompt: text }, null, 2) + '\n' };
  }
  return { fileName: 'tai-system-prompt.txt', mime: 'text/plain;charset=utf-8', content: text };
}
