// MCP 工具结果解析：{content:[{type:'text',text}], isError?, structuredContent?}

export function mcpTexts(result) {
  if (!result || typeof result !== 'object' || !Array.isArray(result.content)) return [];
  return result.content
    .filter((item) => item && item.type === 'text' && typeof item.text === 'string')
    .map((item) => item.text);
}

export function tryParseJson(text) {
  if (typeof text !== 'string') return { ok: false };
  const t = text.trim();
  if (!t || (t[0] !== '{' && t[0] !== '[')) return { ok: false };
  try {
    return { ok: true, value: JSON.parse(t) };
  } catch {
    return { ok: false };
  }
}

/**
 * @returns {{isError:boolean, texts:string[], data:any, source:'structured'|'text-json'|'raw'|null, otherContent:any[]}}
 */
export function interpretMcp(result) {
  const isMcp = !!result && typeof result === 'object' && Array.isArray(result.content);
  const isError = !!(result && typeof result === 'object' && result.isError);
  const texts = mcpTexts(result);
  const otherContent = isMcp ? result.content.filter((c) => !c || c.type !== 'text') : [];
  let data;
  let source = null;

  if (result && typeof result === 'object' && result.structuredContent && typeof result.structuredContent === 'object') {
    data = result.structuredContent;
    source = 'structured';
  } else if (texts.length === 1) {
    const parsed = tryParseJson(texts[0]);
    if (parsed.ok) {
      data = parsed.value;
      source = 'text-json';
    }
  } else if (texts.length > 1) {
    const parsed = texts.map(tryParseJson);
    if (parsed.every((p) => p.ok)) {
      data = parsed.map((p) => p.value);
      source = 'text-json';
    }
  }

  if (data === undefined && !isMcp && result !== undefined && result !== null) {
    data = result;
    source = 'raw';
  }
  return { isError, texts, data, source, otherContent };
}

const ATTRIBUTE_STATUS = {
  NotReadable: '不可读',
  ReadFailed: '读取失败',
  ProjectionFailed: '值无法转换',
  NotRead: '未读取',
};

const pick = (o, k) => (o[k] !== undefined ? o[k] : o[k[0].toLowerCase() + k.slice(1)]);

/**
 * 属性读取诊断：后端在部分属性不可读时仍返回其余属性，Meta 中标记 partial / attributeFailureCount，
 * 每个失败属性带 Status 与 Error。通用结构视图会把它们埋在深层节点里，这里单独提取。
 * @returns {{partial:boolean, failureCount:number, failures:{owner:string, name:string, status:string, statusLabel:string, error:string}[]}}
 */
export function attributeDiagnostics(result) {
  const { data } = interpretMcp(result);
  const failures = [];
  let reported = 0;
  const walk = (node, owner, depth) => {
    if (!node || typeof node !== 'object' || depth > 6 || failures.length >= 200) return;
    if (Array.isArray(node)) {
      for (const item of node) walk(item, owner, depth + 1);
      return;
    }
    const meta = pick(node, 'Meta');
    if (meta && typeof meta === 'object') {
      const n = Number(meta.attributeFailureCount);
      if (Number.isFinite(n) && n > 0) reported += n;
    }
    const name = pick(node, 'Name');
    const here = typeof name === 'string' && name ? name : owner;
    for (const attrs of [pick(node, 'Attributes'), pick(node, 'Properties')]) {
      if (!Array.isArray(attrs)) continue;
      for (const a of attrs) {
        if (!a || typeof a !== 'object') continue;
        const status = pick(a, 'Status');
        if (typeof status !== 'string' || status === 'Success') continue;
        const err = pick(a, 'Error');
        failures.push({
          owner: here,
          name: String(pick(a, 'Name') ?? '?'),
          status,
          statusLabel: ATTRIBUTE_STATUS[status] || status,
          error: typeof err === 'string' ? err : '',
        });
      }
    }
    for (const [k, v] of Object.entries(node)) {
      if (SKIP_WALK.has(k)) continue;
      if (v && typeof v === 'object') walk(v, here, depth + 1);
    }
  };
  walk(data, '', 0);
  const failureCount = Math.max(reported, failures.length);
  // Meta.partial 也可能由属性以外的原因引起（例如枚举失败），由 resultCompleteness 单独展示，避免误报为属性失败。
  return { partial: failureCount > 0, failureCount, failures };
}

const SKIP_WALK = new Set(['Attributes', 'attributes', 'Properties', 'properties', 'Meta', 'meta']);

const UNAVAILABLE_RE = /^\s*(unavailable|unsupported|not supported|not available)\b/i;

function unavailableCapabilities(caps) {
  if (!caps || typeof caps !== 'object' || Array.isArray(caps)) return [];
  const out = [];
  for (const [key, v] of Object.entries(caps)) {
    let text = '';
    if (v === false) text = '';
    else if (typeof v === 'string' && UNAVAILABLE_RE.test(v)) text = v;
    else if (v && typeof v === 'object' && !Array.isArray(v) && (v.supported === false || v.available === false)) {
      const r = v.reason ?? v.message ?? v.detail;
      text = typeof r === 'string' ? r : '';
    } else continue;
    out.push({ key, text });
  }
  return out;
}

/**
 * 结果完整性：分页、截断、部分结果、不支持与不可用能力等。仅依据后端显式返回的字段，
 * 不推断缺失信息；字段可位于顶层或 Meta 中。
 * @returns {{partial:boolean, unsupported:boolean, incomplete:boolean, hasMore:boolean, truncated:boolean,
 *   nextOffset:number|null, offset:number|null, returned:number|null, unavailable:{key:string,text:string}[],
 *   diagnostics:string[], diagnosticCount:number, notes:string[], notable:boolean}}
 */
export function resultCompleteness(result) {
  const out = {
    partial: false, unsupported: false, incomplete: false, hasMore: false, truncated: false,
    nextOffset: null, offset: null, returned: null, unavailable: [], diagnostics: [], diagnosticCount: 0, notes: [], notable: false,
  };
  const { data } = interpretMcp(result);
  if (!data || typeof data !== 'object' || Array.isArray(data)) return out;
  const metaRaw = pick(data, 'Meta');
  const meta = metaRaw && typeof metaRaw === 'object' && !Array.isArray(metaRaw) ? metaRaw : {};
  const flag = (k) => (data[k] !== undefined ? data[k] : meta[k]);
  const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);

  out.partial = flag('partial') === true;
  out.unsupported = flag('supported') === false;
  out.hasMore = flag('hasMore') === true;
  out.truncated = flag('truncated') === true;
  out.nextOffset = num(flag('nextOffset'));
  out.offset = num(flag('offset'));
  const records = pick(data, 'Records');
  if (Array.isArray(records)) out.returned = records.length;
  else out.returned = num(flag('returnedChars'));
  const paged = out.hasMore || out.truncated || out.nextOffset !== null;
  out.incomplete = flag('complete') === false && !paged && flag('endOfDocument') !== true;
  out.unavailable = unavailableCapabilities(pick(data, 'Capabilities'));

  const diags = pick(data, 'Diagnostics');
  if (Array.isArray(diags)) out.diagnostics = diags.filter((d) => typeof d === 'string' && d).slice(0, 50);
  const err = data.error;
  if (typeof err === 'string' && err.trim()) out.diagnostics.unshift(err.trim());
  const n = Number(meta.diagnosticCount);
  out.diagnosticCount = Math.max(Number.isFinite(n) ? n : 0, out.diagnostics.length);
  if (typeof data.cleanupWarning === 'string' && data.cleanupWarning.trim()) out.notes.push(data.cleanupWarning.trim());

  out.notable = out.partial || out.unsupported || out.incomplete || paged || out.unavailable.length > 0 || out.diagnosticCount > 0 || out.notes.length > 0;
  return out;
}

/** 完整性的简短中文标签（用于摘要）。 */
export function completenessTags(c) {
  const tags = [];
  if (c.unsupported) tags.push('不支持');
  if (c.partial) tags.push('部分结果');
  else if (c.incomplete) tags.push('未完整读取');
  if (c.truncated) tags.push('已截断');
  if (c.hasMore || c.nextOffset !== null) tags.push(c.nextOffset !== null ? `有后续页 nextOffset=${c.nextOffset}` : '有后续页');
  return tags;
}

/** 结果的简短摘要（用于提示） */
export function mcpSummary(result, max = 160) {
  const { texts, data } = interpretMcp(result);
  let s = '';
  if (data && typeof data === 'object' && !Array.isArray(data)) {
    s = data.message || data.Message || data.error || data.Error || data.status || data.state || '';
    if (typeof s !== 'string') s = JSON.stringify(s);
  }
  if (!s) s = texts.join(' ');
  s = String(s).replace(/\s+/g, ' ').trim();
  const diag = attributeDiagnostics(result);
  if (diag.partial) s = `部分属性读取失败（${diag.failureCount || '若干'} 项）${s ? '；' + s : ''}`;
  const c = resultCompleteness(result);
  // 属性失败已由上面的前缀说明，避免重复标注“部分结果”。
  const tags = completenessTags(diag.partial ? { ...c, partial: false } : c);
  if (tags.length) s = `[${tags.join('，')}] ${s}`.trim();
  return s.length > max ? s.slice(0, max) + '…' : s;
}
