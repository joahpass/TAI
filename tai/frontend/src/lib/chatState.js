// /api/chat NDJSON 事件 → 进行中任务（draft）的纯函数状态机，便于测试。

export function createDraft(now = new Date().toISOString()) {
  return { role: 'assistant', content: '', tools: [], status: '正在提交任务…', step: null, done: false, error: '', createdAt: now };
}

/** 解析后端状态消息中的“x/y”轮数，如“模型处理中 · 2/12”。 */
export function parseStep(message) {
  const m = String(message ?? '').match(/(\d+)\s*\/\s*(\d+)/);
  if (!m) return null;
  const current = Number(m[1]);
  const total = Number(m[2]);
  return total > 0 && current >= 0 ? { current, total } : null;
}

export function applyChatEvent(draft, ev) {
  if (!draft || !ev || typeof ev !== 'object') return draft;
  switch (ev.type) {
    case 'status':
      return { ...draft, status: String(ev.message ?? ''), step: parseStep(ev.message) ?? draft.step };
    case 'text': {
      const add = String(ev.text ?? '');
      const sep = draft.content && !draft.content.endsWith('\n') && add ? '\n' : '';
      return { ...draft, content: draft.content + sep + add };
    }
    case 'tool_start':
      return {
        ...draft,
        status: `正在执行 ${ev.name}…`,
        tools: [...draft.tools, { id: ev.id, name: ev.name, arguments: ev.arguments, state: 'running' }],
      };
    case 'tool_end': {
      const done = { id: ev.id, name: ev.name, arguments: ev.arguments, result: ev.result, isError: Boolean(ev.isError), state: 'done' };
      const exists = draft.tools.some((t) => t.id === ev.id && t.state === 'running');
      return {
        ...draft,
        tools: exists
          ? draft.tools.map((t) => (t.id === ev.id ? { ...t, ...done, arguments: done.arguments ?? t.arguments } : t))
          : [...draft.tools, done],
      };
    }
    case 'error':
      return { ...draft, error: String(ev.error ?? '未知错误'), status: '' };
    case 'done':
      return { ...draft, status: '', done: true };
    default:
      return draft;
  }
}

/** 工具执行统计，用于进度摘要。 */
export function toolStats(tools = []) {
  let running = 0;
  let ok = 0;
  let failed = 0;
  for (const t of tools) {
    if (t.state === 'running') running += 1;
    else if (t.isError) failed += 1;
    else ok += 1;
  }
  return { running, ok, failed, total: tools.length };
}
