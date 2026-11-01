import { useState } from 'react';
import { ChevronDown, ChevronRight, CircleStop, LoaderCircle, PencilLine, TriangleAlert } from 'lucide-react';
import Markdown from '../components/Markdown.jsx';
import ToolCall from '../components/ToolCall.jsx';
import { CopyButton } from '../components/ResultView.jsx';
import { cx, formatFullTime, MODE_INFO, normalizePersistedTool } from '../utils.js';
import { toolStats } from '../lib/chatState.js';
import { projectChanges } from '../lib/toolLabels.js';

function normTools(tools) {
  return (tools ?? []).map((t) => (t.state ? t : normalizePersistedTool(t)));
}

function Progress({ draft, onCancel, cancelling }) {
  const stats = toolStats(draft.tools);
  const pct = draft.step ? Math.min(100, Math.round((draft.step.current / draft.step.total) * 100)) : null;
  return (
    <div className="as-progress" role="status" aria-live="polite">
      <div className="as-progress-row">
        <LoaderCircle size={14} className="spin" />
        <span className="as-progress-text">{draft.status || '处理中…'}</span>
        <button type="button" className="btn btn-xs btn-danger" onClick={onCancel} disabled={cancelling}>
          <CircleStop size={13} />
          {cancelling ? '正在取消' : '取消'}
        </button>
      </div>
      {pct !== null && (
        <div className="as-bar" title={`工具调用轮数 ${draft.step.current}/${draft.step.total}`}>
          <span style={{ width: `${pct}%` }} />
        </div>
      )}
      {stats.total > 0 && (
        <div className="as-progress-meta small muted">
          工具 {stats.total} 次 · 成功 {stats.ok}
          {stats.failed > 0 && <span className="err-text"> · 失败 {stats.failed}</span>}
          {stats.running > 0 && <span> · 执行中 {stats.running}</span>}
        </div>
      )}
      {cancelling && <div className="small warn-text">已请求取消：已开始的博途操作可能仍会完成，结束后请刷新工程状态。</div>}
    </div>
  );
}

function ToolList({ tools, live }) {
  const [open, setOpen] = useState(false);
  if (!tools.length) return null;
  const stats = toolStats(tools);
  const expanded = live || open;
  return (
    <div className="as-tools">
      {!live && (
        <button type="button" className="as-tools-toggle" onClick={() => setOpen((v) => !v)} aria-expanded={open}>
          {open ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
          <span>执行了 {stats.total} 个工具调用</span>
          {stats.failed > 0 ? <span className="tag tag-err">失败 {stats.failed}</span> : <span className="tag tag-ok">全部成功</span>}
        </button>
      )}
      {expanded && (
        <div className="tools">
          {tools.map((t, i) => (
            <ToolCall key={t.id || i} tool={t} />
          ))}
        </div>
      )}
    </div>
  );
}

function Changes({ tools }) {
  const changes = projectChanges(tools);
  if (!changes.length) return null;
  return (
    <div className="alert alert-warn as-changes">
      <PencilLine size={15} />
      <span>
        本次任务已修改博途工程：{changes.map((c) => `${c.label}${c.count > 1 ? ` ×${c.count}` : ''}`).join('、')}。
        TAI 不能自动撤销；修改前的备份路径与编译结果见工具详情。
      </span>
    </div>
  );
}

function AssistantMessage({ msg, live, onCancel, cancelling }) {
  const tools = normTools(msg.tools);
  return (
    <div className="as-msg as-msg-ai">
      <div className="as-meta">
        <span className="strong">TAI</span>
        {msg.createdAt && <span className="muted">{formatFullTime(msg.createdAt)}</span>}
        {!live && msg.content && <CopyButton text={msg.content} className="msg-copy" />}
      </div>
      <ToolList tools={tools} live={live} />
      {msg.content ? <Markdown text={msg.content} /> : null}
      {live && !msg.done && !msg.error && <Progress draft={msg} onCancel={onCancel} cancelling={cancelling} />}
      {msg.error && (
        <div className="alert alert-err">
          <TriangleAlert size={15} />
          <span>{msg.error}</span>
        </div>
      )}
      {!live && <Changes tools={tools} />}
    </div>
  );
}

function UserMessage({ msg }) {
  return (
    <div className="as-msg as-msg-user">
      <div className="as-meta">
        <span className="strong">任务</span>
        {msg.createdAt && <span className="muted">{formatFullTime(msg.createdAt)}</span>}
        {msg.mode && <span className={cx('tag', msg.mode === 'write' ? 'tag-warn' : 'tag-info')}>{MODE_INFO[msg.mode].label}</span>}
      </div>
      <div className="user-text">{msg.content}</div>
    </div>
  );
}

export default function Thread({ messages, draft, onCancel, cancelling }) {
  return (
    <div className="as-thread">
      {messages.map((m, i) =>
        m.role === 'user' ? <UserMessage key={i} msg={m} /> : <AssistantMessage key={i} msg={m} />,
      )}
      {draft && <AssistantMessage msg={draft} live onCancel={onCancel} cancelling={cancelling} />}
    </div>
  );
}
