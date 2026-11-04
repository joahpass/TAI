// 配置表单：与 server/store.mjs validateConfig 的规则保持一致，在提交前给出字段级错误。
// 工具调用轮数不在界面配置：后端始终不限轮数（maxSteps 0），请求体不发送 maxSteps。

import { REASONING_OPTIONS } from './taskOptions.js';
export const PRESET_URL = { openai: 'https://api.openai.com/v1', anthropic: 'https://api.anthropic.com' };

export const MCP_KEYS = ['mcpPath', 'tiaLocation', 'tiaVersion'];
const TEXT_KEYS = ['provider', 'baseUrl', 'model', 'mcpPath', 'tiaLocation'];
const NUMBERS = [
  ['temperature', 0, 2, false, '温度'],
  ['maxTokens', 128, 65536, true, '最大输出 Token'],
  ['tiaVersion', 19, 30, true, 'TIA 主版本'],
];
export const FORM_KEYS = [...TEXT_KEYS, 'reasoningEffort', ...NUMBERS.map((n) => n[0])];

/** 公共配置 → 表单（数字字段以字符串编辑）。 */
export function toForm(config) {
  const form = {};
  for (const k of TEXT_KEYS) form[k] = String(config?.[k] ?? '');
  for (const [k] of NUMBERS) form[k] = config?.[k] === undefined || config?.[k] === null ? '' : String(config[k]);
  form.reasoningEffort = config?.reasoningEffort ?? 'auto';
  return form;
}

export function validateForm(form) {
  const errors = {};
  if (!REASONING_OPTIONS.some(([id]) => id === form.reasoningEffort)) errors.reasoningEffort = '请选择思考程度';
  if (!['openai', 'anthropic'].includes(form.provider)) errors.provider = '请选择模型协议';
  try {
    const u = new URL(String(form.baseUrl).trim());
    if (!['http:', 'https:'].includes(u.protocol) || u.username || u.password || u.search || u.hash) throw new Error('invalid');
  } catch {
    errors.baseUrl = '请输入有效的 HTTP(S) 基础地址（不含账号、查询参数或 #）';
  }
  for (const [k, min, max, integer, label] of NUMBERS) {
    const s = String(form[k] ?? '').trim();
    const n = Number(s);
    if (!s || !Number.isFinite(n) || n < min || n > max || (integer && !Number.isInteger(n))) {
      errors[k] = `${label}取值 ${min}–${max}${integer ? ' 的整数' : ''}`;
    }
  }
  if (!errors.tiaVersion && ![19, 21].includes(Number(form.tiaVersion))) errors.tiaVersion = '请选择已打包支持的 TIA V19 或 V21';
  if (!String(form.mcpPath ?? '').trim()) errors.mcpPath = '请填写 MCP 可执行文件路径';
  if (!String(form.tiaLocation ?? '').trim()) errors.tiaLocation = '请填写 TIA Portal 安装路径';
  return errors;
}

/** 表单 → PUT /api/config 请求体。API Key 留空表示保留。 */
export function buildConfigBody(form, { apiKey = '', clearKey = false } = {}) {
  const body = {};
  for (const k of TEXT_KEYS) body[k] = String(form[k] ?? '').trim();
  for (const [k] of NUMBERS) body[k] = Number(String(form[k]).trim());
  body.reasoningEffort = form.reasoningEffort ?? 'auto';
  if (clearKey) body.clearApiKey = true;
  else if (String(apiKey).trim()) body.apiKey = String(apiKey).trim();
  return body;
}

export function changedKeys(form, config) {
  const base = toForm(config);
  return FORM_KEYS.filter((k) => String(form[k] ?? '').trim() !== String(base[k] ?? '').trim());
}

/** 与 server/model.mjs endpoint() 相同的实际请求地址，用于提示。 */
export function endpointPreview(provider, baseUrl) {
  const base = String(baseUrl ?? '').trim().replace(/\/+$/, '');
  if (!base) return '';
  return base + (provider === 'anthropic' ? (base.endsWith('/v1') ? '/messages' : '/v1/messages') : '/chat/completions');
}
