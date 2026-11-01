import test from 'node:test';
import assert from 'node:assert/strict';
import { surfaceFor, consoleUrl, assistantUrl, ASSISTANT_PATH } from '../src/lib/route.js';
import { applyChatEvent, createDraft, parseStep, toolStats } from '../src/lib/chatState.js';
import { buildConfigBody, changedKeys, endpointPreview, toForm, validateForm } from '../src/lib/configForm.js';
import { needsConfirm, projectChanges, modifiesProject, toolLabel, isReadOnlyArtifactTool } from '../src/lib/toolLabels.js';
import { connectedFromState } from '../src/lib/context.js';
import { attributeDiagnostics, mcpSummary, resultCompleteness } from '../src/mcp.js';

test('路由：/assistant 及其变体进入助手界面', () => {
  for (const p of ['/assistant', '/assistant/', '/Assistant', '//assistant', '/assistant/history', '/assistant?x=1'.split('?')[0]]) {
    assert.equal(surfaceFor(p), 'assistant', p);
  }
});

test('路由：其他路径进入配置控制台', () => {
  for (const p of ['/', '', undefined, '/index.html', '/assistants', '/settings', '/api/status', '/x/assistant']) {
    assert.equal(surfaceFor(p), 'console', String(p));
  }
});

test('路由：控制台与助手地址同源', () => {
  assert.equal(consoleUrl('http://127.0.0.1:17821'), 'http://127.0.0.1:17821/');
  assert.equal(assistantUrl('http://127.0.0.1:17821'), `http://127.0.0.1:17821${ASSISTANT_PATH}`);
  assert.equal(consoleUrl('http://localhost:5173/'), 'http://localhost:5173/');
});

test('NDJSON 事件归约：进度、工具、文本、完成', () => {
  let d = createDraft('2026-01-01T00:00:00Z');
  d = applyChatEvent(d, { type: 'status', message: '模型处理中 · 2/12' });
  assert.deepEqual(d.step, parseStep('2/12'));
  d = applyChatEvent(d, { type: 'tool_start', id: 'a', name: 'GetBlocks', arguments: {} });
  assert.equal(toolStats(d.tools).running, 1);
  d = applyChatEvent(d, { type: 'tool_end', id: 'a', name: 'GetBlocks', result: { content: [] }, isError: false });
  d = applyChatEvent(d, { type: 'tool_start', id: 'b', name: 'ImportBlock', arguments: {} });
  d = applyChatEvent(d, { type: 'tool_end', id: 'b', name: 'ImportBlock', result: {}, isError: true });
  const s = toolStats(d.tools);
  assert.equal(s.total, 2);
  assert.equal(s.running, 0);
  assert.equal(s.failed, 1);
  assert.equal(s.ok, 1);
  d = applyChatEvent(d, { type: 'text', text: '完成' });
  assert.match(d.content, /完成/);
  d = applyChatEvent(d, { type: 'done', conversationId: 'c' });
  assert.equal(d.done, true);
  assert.equal(d.status, '');
});

test('NDJSON 事件归约：错误事件保留服务端原文', () => {
  const d = applyChatEvent(createDraft('t'), { type: 'error', error: '任务已取消；已开始的博途操作可能已经完成，请刷新工程状态。' });
  assert.match(d.error, /已开始的博途操作可能已经完成/);
});

test('配置表单：与服务端校验范围一致', () => {
  const base = {
    provider: 'openai', baseUrl: 'https://api.openai.com/v1', model: 'm', hasApiKey: true,
    temperature: 0.2, maxTokens: 4096, maxSteps: 0, mcpPath: 'C:/mcp.exe', tiaLocation: 'C:/TIA', tiaVersion: 19,
  };
  const form = toForm(base);
  assert.deepEqual(validateForm(form), {});
  // 工具轮数不再由界面限制：表单、校验、请求体、变更检测均不含 maxSteps（后端固定 0 = 不限）。
  assert.equal('maxSteps' in form, false);
  assert.equal('maxSteps' in validateForm({ ...form, maxSteps: '0' }), false);
  assert.equal('maxSteps' in buildConfigBody({ ...form, maxSteps: '31' }), false);
  assert.deepEqual(changedKeys(form, { ...base, maxSteps: 99 }), []);
  assert.ok(validateForm({ ...form, maxTokens: '100' }).maxTokens);
  assert.ok(validateForm({ ...form, temperature: '2.5' }).temperature);
  assert.deepEqual(validateForm({ ...form, tiaVersion: '19' }), {});
  assert.equal(buildConfigBody({ ...form, tiaVersion: '19' }).tiaVersion, 19);
  assert.ok(validateForm({ ...form, tiaVersion: '18' }).tiaVersion);
  assert.ok(validateForm({ ...form, tiaVersion: '20' }).tiaVersion);
  assert.ok(validateForm({ ...form, tiaVersion: '22' }).tiaVersion);
  assert.ok(validateForm({ ...form, baseUrl: 'ftp://x' }).baseUrl);
  assert.deepEqual(changedKeys(form, base), []);
  assert.deepEqual(changedKeys({ ...form, tiaVersion: '21' }, base), ['tiaVersion']);
});

test('配置表单：API Key 留空保留、可显式清除', () => {
  const form = toForm({ provider: 'anthropic', baseUrl: 'https://api.anthropic.com', model: 'x', temperature: 0, maxTokens: 1024, mcpPath: 'a', tiaLocation: 'b', tiaVersion: 19 });
  assert.equal('apiKey' in buildConfigBody(form, { apiKey: '  ', clearKey: false }), false);
  assert.equal(buildConfigBody(form, { apiKey: 'k', clearKey: false }).apiKey, 'k');
  const cleared = buildConfigBody(form, { apiKey: 'k', clearKey: true });
  assert.equal(cleared.clearApiKey, true);
  assert.equal('apiKey' in cleared, false);
  assert.equal(endpointPreview('anthropic', 'https://api.anthropic.com'), 'https://api.anthropic.com/v1/messages');
  assert.equal(endpointPreview('openai', 'https://api.openai.com/v1'), 'https://api.openai.com/v1/chat/completions');
});

test('工具标签：写入/生命周期工具需要确认，导入/保存计入工程变更', () => {
  assert.equal(needsConfirm({ name: 'GetBlocks', effect: 'read' }), false);
  assert.equal(needsConfirm({ name: 'CompileSoftware', effect: 'write' }), true);
  assert.equal(needsConfirm({ name: 'OpenProject', effect: 'lifecycle' }), true);
  assert.equal(modifiesProject('ImportBlock'), true);
  assert.equal(modifiesProject('ExportBlock'), false);
  const changes = projectChanges([
    { name: 'ImportBlock', isError: false },
    { name: 'ImportBlock', isError: false },
    { name: 'ImportBlock', isError: true },
    { name: 'SaveProject', state: 'running' },
    { name: 'GetBlocks' },
  ]);
  assert.deepEqual(changes.map((c) => [c.name, c.count]), [['ImportBlock', 2]]);
  assert.notEqual(toolLabel('GetBlocks'), '');
});

test('工具标签：块删除/重命名计入工程变更，备份与工件编辑不计入', () => {
  for (const name of ['DeleteBlock', 'RenameBlock', 'BackupSoftwareForEdit', 'TAI_EditArtifact']) {
    assert.notEqual(toolLabel(name), name, name);
  }
  assert.equal(modifiesProject('DeleteBlock'), true);
  assert.equal(modifiesProject('RenameBlock'), true);
  assert.equal(modifiesProject('BackupSoftwareForEdit'), false);
  assert.equal(modifiesProject('TAI_EditArtifact'), false);
  assert.equal(needsConfirm({ name: 'DeleteBlock', effect: 'write' }), true);
});

test('属性诊断：部分读取失败的属性被单独提取并进入摘要', () => {
  const payload = {
    Message: 'Device info retrieved',
    Name: 'PLC_1',
    Attributes: [
      { Name: 'Author', Status: 'Success', Value: 'x' },
      { Name: 'Locked', Status: 'ReadFailed', Error: 'System.InvalidOperationException: Read denied' },
      { Name: 'Blob', Status: 'ProjectionFailed', Error: 'no JSON-safe projection' },
    ],
    Meta: { success: false, partial: true, attributesComplete: false, attributeFailureCount: 2 },
  };
  const result = { content: [{ type: 'text', text: JSON.stringify(payload) }], isError: true };
  const d = attributeDiagnostics(result);
  assert.equal(d.partial, true);
  assert.equal(d.failureCount, 2);
  assert.deepEqual(d.failures.map((f) => [f.owner, f.name, f.statusLabel]), [['PLC_1', 'Locked', '读取失败'], ['PLC_1', 'Blob', '值无法转换']]);
  assert.match(mcpSummary(result), /^部分属性读取失败（2 项）；Device info retrieved/);
  const nested = { structuredContent: { Items: [{ Name: 'P', Attributes: [{ Name: 'W', Status: 'NotReadable' }] }], Meta: { attributeFailureCount: 1, partial: true } } };
  assert.deepEqual(attributeDiagnostics(nested).failures.map((f) => [f.owner, f.name]), [['P', 'W']]);
  const healthy = { content: [{ type: 'text', text: JSON.stringify({ Attributes: [{ Name: 'A', Status: 'Success' }], Meta: { success: true, attributesComplete: true, attributeFailureCount: 0 } }) }] };
  assert.equal(attributeDiagnostics(healthy).partial, false);
  assert.equal(mcpSummary({ content: [{ type: 'text', text: 'plain' }] }), 'plain');
});

test('工具标签：HMI 只读工具使用中文工程名称且不触发写入确认', () => {
  const hmi = {
    GetHmiSoftware: 'HMI软件',
    GetHmiScreens: 'HMI画面',
    GetHmiTagTables: 'HMI变量表',
    GetHmiConnections: 'HMI连接',
    GetHmiScreenContent: 'HMI画面内容',
  };
  for (const [name, label] of Object.entries(hmi)) {
    assert.equal(toolLabel(name), label, name);
    assert.equal(needsConfirm({ name, effect: 'read' }), false, name);
    assert.equal(modifiesProject(name), false, name);
    assert.notEqual(toolLabel(name), name, name);
  }
  assert.deepEqual(projectChanges(Object.keys(hmi).map((name) => ({ name, isError: false }))), []);
});

test('工具标签：HMI 编辑工具——读取只读、修改/导入需确认并计入工程变更、导出不改工程', () => {
  const labels = {
    GetHmiEditableObject: '读取HMI可编辑对象',
    SetHmiObjectAttributes: '修改HMI对象属性',
    ExportHmiObject: '导出HMI对象',
    ImportHmiObject: '导入HMI对象',
  };
  for (const [name, label] of Object.entries(labels)) assert.equal(toolLabel(name), label, name);
  // 通用编译名称（PLC 与 HMI 共用）。
  assert.equal(toolLabel('CompileSoftware'), '编译软件');
  assert.doesNotMatch(toolLabel('CompileSoftware'), /PLC|HMI/);

  assert.equal(needsConfirm({ name: 'GetHmiEditableObject', effect: 'read' }), false);
  assert.equal(needsConfirm({ name: 'GetHmiEditableObject' }), false);
  assert.equal(modifiesProject('GetHmiEditableObject'), false);

  for (const name of ['SetHmiObjectAttributes', 'ImportHmiObject']) {
    assert.equal(needsConfirm({ name, effect: 'write' }), true, name);
    assert.equal(needsConfirm({ name }), true, `${name} 无 effect 时仍需确认`);
    assert.equal(modifiesProject(name), true, name);
  }

  assert.equal(modifiesProject('ExportHmiObject'), false);
  assert.equal(needsConfirm({ name: 'ExportHmiObject', effect: 'read' }), false);

  const changes = projectChanges([
    { name: 'GetHmiEditableObject', isError: false },
    { name: 'SetHmiObjectAttributes', isError: false },
    { name: 'SetHmiObjectAttributes', isError: false },
    { name: 'SetHmiObjectAttributes', isError: true },
    { name: 'ExportHmiObject', isError: false },
    { name: 'ImportHmiObject', isError: false },
    { name: 'ImportHmiObject', state: 'running' },
  ]);
  assert.deepEqual(changes.map((c) => [c.name, c.label, c.count]), [
    ['SetHmiObjectAttributes', '修改HMI对象属性', 2],
    ['ImportHmiObject', '导入HMI对象', 1],
  ]);
});

test('工具标签：只读工件工具（列出/分析/搜索）使用中文名称且不计入工程修改', () => {
  const art = {
    TAI_ListArtifacts: '列出工件目录',
    TAI_InspectArtifact: '分析工件结构',
    TAI_SearchArtifact: '搜索工件文本',
  };
  for (const [name, label] of Object.entries(art)) {
    assert.equal(toolLabel(name), label, name);
    assert.equal(isReadOnlyArtifactTool(name), true, name);
    assert.equal(needsConfirm({ name, effect: 'read' }), false, name);
    assert.equal(needsConfirm({ name, effect: 'write' }), false, name);
    assert.equal(modifiesProject(name), false, name);
  }
  assert.equal(isReadOnlyArtifactTool('TAI_WriteArtifact'), false);
  assert.equal(isReadOnlyArtifactTool('TAI_EditArtifact'), false);
  const changes = projectChanges([
    ...Object.keys(art).map((name) => ({ name, isError: false })),
    { name: 'ImportBlock', isError: false },
  ]);
  assert.deepEqual(changes.map((c) => [c.name, c.count]), [['ImportBlock', 1]]);
});

test('完整性：分页/截断/部分/不支持只在后端显式标记时展示', () => {
  const of = (obj) => resultCompleteness({ content: [{ type: 'text', text: JSON.stringify(obj) }] });
  const paged = of({ records: [{ path: 'a' }], offset: 10, limit: 1, hasMore: true, nextOffset: 11, complete: false, Meta: { success: true, partial: false, hasMore: true } });
  assert.equal(paged.hasMore, true);
  assert.equal(paged.nextOffset, 11);
  assert.equal(paged.offset, 10);
  assert.equal(paged.returned, 1);
  assert.equal(paged.incomplete, false);
  assert.equal(paged.partial, false);
  assert.equal(paged.notable, true);
  assert.match(mcpSummary({ content: [{ type: 'text', text: JSON.stringify({ records: [], offset: 0, nextOffset: 100, hasMore: true }) }] }), /nextOffset=100/);

  const unavail = of({ capabilities: { screenItems: 'Unavailable: Classic exposes no typed item composition', runtimeValues: 'Unavailable: engineering only', screens: 'Available: screens' } });
  assert.deepEqual(unavail.unavailable.map((u) => u.key), ['screenItems', 'runtimeValues']);
  assert.equal(unavail.notable, true);

  const unsup = of({ supported: false, message: 'nope' });
  assert.equal(unsup.unsupported, true);
  assert.match(mcpSummary({ content: [{ type: 'text', text: JSON.stringify({ supported: false }) }] }), /^\[不支持\]/);

  // 最后一页（endOfDocument）不应被标为未完整读取。
  const lastPage = of({ complete: false, endOfDocument: true });
  assert.equal(lastPage.incomplete, false);
  assert.equal(lastPage.notable, false);

  const truncated = of({ truncated: true, returnedChars: 20000 });
  assert.equal(truncated.truncated, true);
  assert.equal(truncated.returned, 20000);

  const plain = of({ records: [], complete: true, Meta: { success: true, partial: false } });
  assert.equal(plain.notable, false);
  assert.equal(resultCompleteness({ content: [{ type: 'text', text: 'plain' }] }).notable, false);
});

test('属性诊断：Meta.partial 由其他原因引起时不误报为属性失败', () => {
  const result = { content: [{ type: 'text', text: JSON.stringify({ records: [], Meta: { success: false, partial: true, diagnosticCount: 3 }, diagnostics: ['enumeration stopped'] }) }] };
  const d = attributeDiagnostics(result);
  assert.equal(d.partial, false);
  assert.equal(d.failureCount, 0);
  const c = resultCompleteness(result);
  assert.equal(c.partial, true);
  assert.equal(c.diagnosticCount, 3);
  assert.deepEqual(c.diagnostics, ['enumeration stopped']);
});

test('连接状态解析：仅在结果明确时给出结论', () => {
  assert.equal(connectedFromState({ content: [{ type: 'text', text: '{"IsConnected":true}' }] }), true);
  assert.equal(connectedFromState({ structuredContent: { isConnected: false } }), false);
  assert.equal(connectedFromState({ content: [{ type: 'text', text: 'hello' }] }), null);
});
