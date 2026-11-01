import test from 'node:test';
import assert from 'node:assert/strict';
import { createNdjsonParser } from '../src/ndjson.js';
import { interpretMcp } from '../src/mcp.js';

test('NDJSON 解析可处理跨 chunk、多字节字符与无尾换行', () => {
  const events = [];
  const bad = [];
  const p = createNdjsonParser((e) => events.push(e), (l) => bad.push(l));
  const bytes = new TextEncoder().encode('{"type":"text","text":"博途"}\r\n{"type":"st');
  p.push(bytes.slice(0, 26)); // 截断在中文字符中间
  p.push(bytes.slice(26));
  p.push('atus","message":"x"}\nnot json\n{"type":"done"}');
  p.end();
  assert.deepEqual(events, [{ type: 'text', text: '博途' }, { type: 'status', message: 'x' }, { type: 'done' }]);
  assert.deepEqual(bad, ['not json']);
});

test('MCP 结果解析文本 JSON 与错误标记', () => {
  const r = interpretMcp({ content: [{ type: 'text', text: '{"IsConnected":true}' }] });
  assert.equal(r.source, 'text-json');
  assert.equal(r.data.IsConnected, true);
  assert.equal(interpretMcp({ isError: true, content: [{ type: 'text', text: '失败' }] }).isError, true);
});
