import test from 'node:test';
import assert from 'node:assert/strict';
import { closeTab, editDraft, editHomeDraft, openTab, restoreView, selectWorkspace, showHome, sortedDesigns } from '../src/workspace/state.js';
import { surfaceFor } from '../src/lib/route.js';

test('工作台、紧凑助手与原配置入口兼容', () => {
  assert.equal(surfaceFor('/workspace'), 'workspace');
  assert.equal(surfaceFor('/workspace/'), 'workspace');
  assert.equal(surfaceFor('/assistant'), 'assistant');
  assert.equal(surfaceFor('/settings'), 'console');
  assert.equal(surfaceFor('/'), 'console');
});
test('切换与关闭标签保留各设计的草稿与模式，重新打开恢复', () => {
  let view = openTab(restoreView(null), 'a');
  view = editDraft(view, 'a', { text: '电机控制', mode: 'write' });
  view = openTab(view, 'b');
  view = editDraft(view, 'b', { text: '检查画面', mode: 'read' });
  view = closeTab(view, 'a');
  assert.equal(view.activeId, 'b');
  view = openTab(view, 'a');
  assert.deepEqual(view.drafts.a, { text: '电机控制', mode: 'write', model: '', reasoningEffort: '' });
  assert.deepEqual(view.drafts.b, { text: '检查画面', mode: 'read', model: '', reasoningEffort: '' });
  assert.equal(closeTab(view, 'a').activeId, 'b');
  assert.equal(closeTab(closeTab(view, 'a'), 'b').activeId, '');
});
test('窗口恢复去重标签，修复过期激活项与主题', () => {
  const view = restoreView({ tabs: ['b', 'a', 'b', null], activeId: 'missing', theme: 'invalid', positions: { a: 42 } });
  assert.deepEqual(view.tabs, ['b', 'a']); assert.equal(view.activeId, 'b');
  assert.equal(view.theme, 'light'); assert.equal(view.positions.a, 42);
  assert.equal(restoreView(JSON.parse(JSON.stringify(editDraft(view, 'b', { text: '保留' })))).drafts.b.text, '保留');
});
test('工作区首页草稿与对话分离并支持旧状态迁移', () => {
  let view = restoreView({ tabs: ['old'], activeId: 'old', drafts: { old: { text: '旧对话' } } });
  view = editHomeDraft(view, 'line', { text: '工作区任务', mode: 'write' });
  view = editHomeDraft(view, 'library', { text: '标准库任务' });
  view = showHome(view, 'line');
  const restored = restoreView(JSON.parse(JSON.stringify(view)));
  assert.equal(restored.activeId, '');
  assert.equal(restored.drafts.old.text, '旧对话');
  assert.deepEqual(restored.homeDrafts.line, { text: '工作区任务', mode: 'write', model: '', reasoningEffort: '' });
  assert.equal(restored.homeDrafts.library.text, '标准库任务');
});
test('切换工作区恢复上次页面，失效或移动的会话回首页', () => {
  const records = [{ id: 'a', projectId: 'line' }, { id: 'b', projectId: 'library' }];
  let view = openTab(restoreView(null), 'a', 'line');
  view = openTab(view, 'b', 'library');
  view = selectWorkspace(view, 'line', records);
  assert.equal(view.activeId, 'a');
  assert.equal(view.projectId, 'line');
  assert.equal(selectWorkspace(view, 'line', [{ id: 'a', projectId: 'elsewhere' }]).activeId, '');
  assert.equal(selectWorkspace(closeTab(view, 'a'), 'line', records).activeId, '');
});
test('设计置顶优先，其余按更新时间排序且保留输入顺序对象', () => {
  const items = [{ id: 'a', updatedAt: '2026-01-03' }, { id: 'b', pinned: true, updatedAt: '2026-01-01' }, { id: 'c', updatedAt: '2026-01-02' }];
  assert.deepEqual(sortedDesigns(items).map(d => d.id), ['b', 'a', 'c']);
  assert.deepEqual(items.map(d => d.id), ['a', 'b', 'c']);
});
