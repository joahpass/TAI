import { useState } from 'react';
import { ChevronDown, ChevronRight, CircleCheck, CircleX, LoaderCircle } from 'lucide-react';
import { mcpSummary } from '../mcp.js';
import { cx, prettyJson, summarizeArgs } from '../utils.js';
import { toolLabel } from '../lib/toolLabels.js';
import { ToolResult } from './ResultView.jsx';

/** 一次工具调用：中文名称 + 状态，展开后显示参数与结果。 */
export default function ToolCall({ tool }) {
  const [open, setOpen] = useState(false);
  const running = tool.state === 'running';
  const err = !running && tool.isError;
  const Icon = running ? LoaderCircle : err ? CircleX : CircleCheck;
  const summary = !running && tool.result !== undefined ? mcpSummary(tool.result, 140) : '';
  const args = summarizeArgs(tool.arguments);
  return (
    <div className={cx('tool', running && 'tool-run', err && 'tool-err')}>
      <button type="button" className="tool-head" onClick={() => setOpen((v) => !v)} aria-expanded={open}>
        {open ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
        <Icon size={14} className={cx('tool-icon', running && 'spin')} aria-label={running ? '执行中' : err ? '失败' : '成功'} />
        <span className="tool-name">{toolLabel(tool.name)}</span>
        <span className="tool-args mono" title={args || tool.name}>
          {open ? tool.name : args || tool.name}
        </span>
      </button>
      {!open && summary && <div className={cx('tool-sum', err && 'err-text')}>{summary}</div>}
      {open && (
        <div className="tool-body">
          <div className="label">参数</div>
          <pre className="args">{prettyJson(tool.arguments ?? {})}</pre>
          <div className="label">结果</div>
          {running ? <div className="muted small">执行中…</div> : <ToolResult result={tool.result} isError={tool.isError} />}
        </div>
      )}
    </div>
  );
}
