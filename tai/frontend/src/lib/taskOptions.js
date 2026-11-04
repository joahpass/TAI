export const REASONING_OPTIONS = [['auto', '模型默认'], ['off', '关闭'], ['low', '低'], ['medium', '中'], ['high', '高']];
export const EMPTY_DRAFT = { text: '', mode: 'read', model: '', reasoningEffort: '' };
export function normalizeDraft(value) {
  const source = value && typeof value === 'object' ? value : {};
  return { ...EMPTY_DRAFT, ...source,
    text: typeof source.text === 'string' ? source.text : '',
    mode: ['read', 'artifact', 'write'].includes(source.mode) ? source.mode : 'read',
    model: typeof source.model === 'string' ? source.model : '',
    reasoningEffort: ['', ...REASONING_OPTIONS.map(([id]) => id)].includes(source.reasoningEffort) ? source.reasoningEffort : '' };
}
export function chatOptions(value) {
  const draft = normalizeDraft(value), model = draft.model.trim();
  return { mode: draft.mode, ...(model ? { model } : {}), ...(draft.reasoningEffort ? { reasoningEffort: draft.reasoningEffort } : {}) };
}
export function reasoningLabel(value) { return REASONING_OPTIONS.find(([id]) => id === value)?.[1] || '跟随配置'; }
