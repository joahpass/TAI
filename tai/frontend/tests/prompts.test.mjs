import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  buildPromptBody,
  decodeUtf8,
  effectivePrompt,
  exportPayload,
  isOverride,
  isPromptDirty,
  MAX_PROMPT_CHARS,
  normalizePrompts,
  parsePromptImport,
  readPromptFile,
  validatePromptDraft,
} from '../src/lib/promptEditor.js';

const DEF = '你是 TAI 助手。\n请谨慎操作。';

test('提示词：无覆盖时载入内置默认，有覆盖时载入覆盖', () => {
  assert.deepEqual(normalizePrompts(null), { systemPrompt: '', defaultSystemPrompt: '' });
  const none = { systemPrompt: '', defaultSystemPrompt: DEF };
  assert.equal(effectivePrompt(none), DEF);
  assert.equal(isOverride(none), false);
  assert.equal(isPromptDirty(DEF, none), false);
  assert.equal(isPromptDirty(DEF.replace('\n', '\r\n'), none), false);
  const custom = { systemPrompt: '自定义', defaultSystemPrompt: DEF };
  assert.equal(effectivePrompt(custom), '自定义');
  assert.equal(isOverride(custom), true);
  assert.equal(isPromptDirty('自定义2', custom), true);
});

test('提示词：保存请求体——空白或等于默认时发送空串（恢复默认）', () => {
  assert.deepEqual(buildPromptBody('', DEF), { systemPrompt: '' });
  assert.deepEqual(buildPromptBody('  \n ', DEF), { systemPrompt: '' });
  assert.deepEqual(buildPromptBody(DEF, DEF), { systemPrompt: '' });
  assert.deepEqual(buildPromptBody('新的', DEF), { systemPrompt: '新的' });
});

test('提示词：长度上限 100000 字符', () => {
  assert.equal(MAX_PROMPT_CHARS, 100000);
  assert.equal(validatePromptDraft('a'.repeat(100000)), '');
  assert.match(validatePromptDraft('a'.repeat(100001)), /上限/);
});

test('提示词导入：txt/md 纯文本、JSON {systemPrompt}', () => {
  assert.deepEqual(parsePromptImport('p.txt', 'a\r\nb'), { systemPrompt: 'a\nb' });
  assert.deepEqual(parsePromptImport('P.MD', '# 标题'), { systemPrompt: '# 标题' });
  assert.deepEqual(parsePromptImport('p.json', JSON.stringify({ systemPrompt: '中文' })), { systemPrompt: '中文' });
  assert.throws(() => parsePromptImport('p.json', '{bad'), /JSON 解析失败/);
  assert.throws(() => parsePromptImport('p.json', '{"prompt":"x"}'), /systemPrompt/);
  assert.throws(() => parsePromptImport('p.json', '["x"]'), /systemPrompt/);
  assert.throws(() => parsePromptImport('p.json', '{"systemPrompt":1}'), /systemPrompt/);
  assert.throws(() => parsePromptImport('p.docx', 'x'), /仅支持/);
  assert.throws(() => parsePromptImport('p.txt', '   '), /为空/);
  assert.throws(() => parsePromptImport('p.txt', 'a'.repeat(100001)), /上限/);
});

test('提示词导入：严格 UTF-8 解码并去除 BOM', () => {
  const bytes = new Uint8Array([0xef, 0xbb, 0xbf, ...new TextEncoder().encode('提示')]);
  assert.equal(decodeUtf8(bytes), '提示');
  assert.throws(() => decodeUtf8(new Uint8Array([0xc4, 0xe3, 0xba, 0xc3])), /UTF-8/); // GBK「你好」
});

test('提示词导入：读取文件对象（含大小限制）', async () => {
  const file = (name, content) => {
    const bytes = new TextEncoder().encode(content);
    return { name, size: bytes.length, arrayBuffer: async () => bytes.buffer };
  };
  assert.deepEqual(await readPromptFile(file('x.json', '{"systemPrompt":"ok"}')), { systemPrompt: 'ok' });
  await assert.rejects(readPromptFile({ name: 'big.txt', size: 2 * 1024 * 1024, arrayBuffer: async () => new ArrayBuffer(0) }), /文件过大/);
  await assert.rejects(readPromptFile(null), /未选择/);
});

test('提示词导出：txt 原文，json 可再次导入', () => {
  const t = exportPayload('abc', 'txt');
  assert.equal(t.content, 'abc');
  assert.match(t.fileName, /\.txt$/);
  const j = exportPayload('多行\n内容', 'json');
  assert.match(j.fileName, /\.json$/);
  assert.deepEqual(parsePromptImport(j.fileName, j.content), { systemPrompt: '多行\n内容' });
});
