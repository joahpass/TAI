import { useEffect, useRef, useState } from 'react';
import { Bot, CircleStop, Eye, LoaderCircle, PencilLine, Send, TriangleAlert, User } from 'lucide-react';
import Markdown from './Markdown.jsx';
import ToolCall from './ToolCall.jsx';
import { CopyButton } from './ResultView.jsx';
import { cx, formatFullTime, MODE_INFO, normalizePersistedTool } from '../utils.js';

const PROMPTS = [
  '读取当前博途工程状态，并概述项目中的设备。',
  '列出 PLC 软件中的所有程序块及其层级。',
  '检查项目结构，指出可能的命名或组织问题。',
  '用 SCL 编写一个电机启停控制 FB（带故障复位），先给出代码，不要导入。',
];

function Message({ msg }) {
  const user = msg.role === 'user';
  const tools = (msg.tools ?? []).map((t) => (t.state ? t : normalizePersistedTool(t)));
  return (
    <div className={cx('msg', user ? 'msg-user' : 'msg-ai')}>
      <div className="avatar">{user ? <User size={16} /> : <Bot size={16} />}</div>
      <div className="msg-body">
        <div className="msg-meta">
          <span>{user ? '你' : 'TAI'}</span>
          {msg.createdAt && <span className="muted">{formatFullTime(msg.createdAt)}</span>}
          {msg.mode && <span className={cx('tag', msg.mode === 'write' ? 'tag-warn' : 'tag-info')}>{MODE_INFO[msg.mode].label}</span>}
          {!user && msg.content && <CopyButton text={msg.content} className="msg-copy" />}
        </div>
        {tools.length > 0 && (
          <div className="tools">
            {tools.map((t, i) => (
              <ToolCall key={t.id || i} tool={t} />
            ))}
          </div>
        )}
        {user ? <div className="user-text">{msg.content}</div> : msg.content ? <Markdown text={msg.content} /> : null}
        {msg.status && (
          <div className="status-line">
            <LoaderCircle size={14} className="spin" />
            {msg.status}
          </div>
        )}
        {msg.error && (
          <div className="alert alert-err">
            <TriangleAlert size={15} />
            <span>{msg.error}</span>
          </div>
        )}
      </div>
    </div>
  );
}

export default function Chat({ conversation, draft, running, mode, setMode, onSend, onCancel, status, notice }) {
  const [text, setText] = useState('');
  const endRef = useRef(null);
  const taRef = useRef(null);
  const messages = conversation?.messages ?? [];

  useEffect(() => {
    endRef.current?.scrollIntoView({ block: 'end' });
  }, [messages.length, draft]);

  const submit = async (value) => {
    const v = (value ?? text).trim();
    if (!v || running) return;
    const ok = await onSend(v);
    if (ok !== false && value === undefined) setText('');
  };

  const modelReady = status?.model?.configured;
  const empty = !messages.length && !draft;

  return (
    <section className="chat">
      <div className="chat-scroll">
        {empty ? (
          <div className="welcome">
            <div className="logo logo-lg">T</div>
            <h1>TAI 工程助手</h1>
            <p className="muted">连接 TIA Portal 后，可让 TAI 读取工程、分析程序结构、生成 SCL 代码，并在写入模式下修改工程。</p>
            {!modelReady && (
              <div className="alert alert-warn">
                <TriangleAlert size={15} />
                <span>尚未配置模型，请先在左下角“设置”中填写模型地址、名称与 API Key。</span>
              </div>
            )}
            {status && !status.mcp.connected && (
              <div className="alert alert-info">
                <span>博途未连接：AI 仍可回答问题和生成代码，但无法读取真实工程。可在右侧面板点击“连接博途”。</span>
              </div>
            )}
            <div className="prompts">
              {PROMPTS.map((p) => (
                <button key={p} type="button" className="prompt" disabled={running || !conversation} onClick={() => submit(p)}>
                  {p}
                </button>
              ))}
            </div>
          </div>
        ) : (
          <div className="msgs">
            {messages.map((m, i) => (
              <Message key={i} msg={m} />
            ))}
            {draft && <Message msg={draft} />}
          </div>
        )}
        <div ref={endRef} />
      </div>

      <div className="composer">
        {notice && (
          <div className="alert alert-err composer-alert">
            <TriangleAlert size={15} />
            <span>{notice}</span>
          </div>
        )}
        <div className="composer-box">
          <textarea
            ref={taRef}
            rows={3}
            value={text}
            placeholder={conversation ? '描述你的工程任务… （Enter 发送，Shift+Enter 换行）' : '请先新建或选择会话'}
            disabled={!conversation}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                e.preventDefault();
                submit();
              }
            }}
            aria-label="消息输入"
            maxLength={40000}
          />
          <div className="composer-bar">
            <div className="seg" role="radiogroup" aria-label="工作模式">
              <button type="button" role="radio" aria-checked={mode === 'read'} className={cx(mode === 'read' && 'on')} onClick={() => setMode('read')} disabled={running}>
                <Eye size={14} />
                读取
              </button>
              <button
                type="button"
                role="radio"
                aria-checked={mode === 'write'}
                className={cx(mode === 'write' && 'on on-warn')}
                onClick={() => setMode('write')}
                disabled={running}
              >
                <PencilLine size={14} />
                写入
              </button>
            </div>
            <span className={cx('mode-desc small', mode === 'write' && 'warn-text')}>{MODE_INFO[mode].desc}</span>
            {running ? (
              <button type="button" className="btn btn-danger" onClick={onCancel}>
                <CircleStop size={16} />
                取消
              </button>
            ) : (
              <button type="button" className="btn btn-primary" onClick={() => submit()} disabled={!text.trim() || !conversation}>
                <Send size={16} />
                发送
              </button>
            )}
          </div>
        </div>
      </div>
    </section>
  );
}
