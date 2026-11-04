import test from 'node:test';
import assert from 'node:assert/strict';
import { normalizeDraft, chatOptions, reasoningLabel } from '../src/lib/taskOptions.js';
import { editDraft, editHomeDraft, closeTab, openTab, restoreView } from '../src/workspace/state.js';
import { toForm, validateForm, buildConfigBody, changedKeys } from '../src/lib/configForm.js';
import { applyChatEvent, createDraft } from '../src/lib/chatState.js';
import { api, streamChat } from '../src/api.js';

test('task事件冻结真实模型配置，后续流事件保留快照', () => {
  const selection = { model: 'server-resolved', provider: 'anthropic', reasoningEffort: 'medium', mode: 'artifact' };
  let draft = applyChatEvent(createDraft('now'), { type: 'task', taskId: 't', ...selection });
  draft = applyChatEvent(draft, { type: 'text', text: '运行结果' });
  draft = applyChatEvent(draft, { type: 'done' });
  for (const [key, value] of Object.entries(selection)) assert.equal(draft[key], value);
  assert.equal(draft.done, true);
});

test('旧草稿缺省权限/模型/程度，非法持久化值回落默认', () => {
  assert.deepEqual(normalizeDraft({ text: '旧任务' }), { text: '旧任务', mode: 'read', model: '', reasoningEffort: '' });
  const view = restoreView({ drafts: { old: { text: '旧任务', mode: 'unknown', reasoningEffort: 'extreme' } }, homeDrafts: { p: { text: '首页' } } });
  assert.equal(view.drafts.old.mode, 'read'); assert.equal(view.drafts.old.reasoningEffort, '');
  assert.equal(view.homeDrafts.p.model, '');
});
test('各首页和会话独立保存三个选项，关闭/重新打开/重载仍恢复', () => {
  let v = restoreView(null);
  v = editHomeDraft(v, 'p', { text: '首页', mode: 'artifact', model: 'custom/p', reasoningEffort: 'high' });
  v = editHomeDraft(v, 'q', { text: '另一首页', model: 'other', reasoningEffort: 'off' });
  v = editDraft(openTab(v, 'a'), 'a', v.homeDrafts.p);
  v = editDraft(openTab(v, 'b'), 'b', { text: '另一个任务', mode: 'write', reasoningEffort: 'auto' });
  v = openTab(restoreView(JSON.parse(JSON.stringify(closeTab(v, 'a')))), 'a');
  assert.deepEqual(v.drafts.a, v.homeDrafts.p);
  assert.equal(v.drafts.b.model, ''); assert.equal(v.drafts.b.reasoningEffort, 'auto');
  assert.equal(v.homeDrafts.q.reasoningEffort, 'off');
  // Capturing a request before later edits leaves the running task's options fixed.
  const options = chatOptions(v.drafts.a);
  const edited = editDraft(v, 'a', { model: 'next', reasoningEffort: 'low' });
  assert.deepEqual(options, { mode: 'artifact', model: 'custom/p', reasoningEffort: 'high' });
  assert.equal(edited.drafts.a.model, 'next');
});
test('配置默认auto与自定义模型可往返，枚举校验不接受跟随配置空值', () => {
  const form = toForm({ provider: 'openai', baseUrl: 'https://example.test/v1', model: 'custom-model', temperature: 0, maxTokens: 1024, mcpPath: 'mcp.exe', tiaLocation: 'TIA', tiaVersion: 19 });
  assert.equal(form.reasoningEffort, 'auto'); assert.deepEqual(validateForm(form), {});
  for (const effort of ['auto', 'off', 'low', 'medium', 'high']) {
    const next = { ...form, reasoningEffort: effort };
    assert.deepEqual(validateForm(next), {}); assert.equal(buildConfigBody(next).reasoningEffort, effort);
  }
  assert.ok(validateForm({ ...form, reasoningEffort: '' }).reasoningEffort);
  assert.ok(validateForm({ ...form, reasoningEffort: 'ultra' }).reasoningEffort);
  assert.deepEqual(changedKeys({ ...form, reasoningEffort: 'high' }, form), ['reasoningEffort']);
  assert.equal(buildConfigBody(form).model, 'custom-model');
  assert.equal(reasoningLabel('auto'), '模型默认'); assert.equal(reasoningLabel(''), '跟随配置');
});
test('streamChat确实传递临时选项，空模型/程度省略，catalog刷新URL正确', async () => {
  const original = globalThis.fetch, captured = [];
  globalThis.fetch = async (url, init) => {
    captured.push({ url, init });
    return url.startsWith('/api/models') ? new Response(JSON.stringify({ models: [], source: 'configured' })) : new Response('{"type":"done"}\n', { headers: { 'Content-Type': 'application/x-ndjson' } });
  };
  try {
    await streamChat({ conversationId: 'c', message: 'task', ...chatOptions({ mode: 'artifact', model: '  custom  ', reasoningEffort: 'off' }) }, { onEvent() {} });
    assert.deepEqual(JSON.parse(captured[0].init.body), { conversationId: 'c', message: 'task', mode: 'artifact', model: 'custom', reasoningEffort: 'off' });
    await streamChat({ conversationId: 'c', message: 'task', model: ' ', reasoningEffort: '', mode: 'read' }, { onEvent() {} });
    assert.deepEqual(JSON.parse(captured[1].init.body), { conversationId: 'c', message: 'task', mode: 'read' });
    await api.models(true); assert.equal(captured[2].url, '/api/models?refresh=1');
    assert.equal(captured.filter(c => c.init.method === 'PUT').length, 0);
  } finally { globalThis.fetch = original; }
});
