import { useCallback, useEffect, useRef, useState } from 'react';
import { Eye, History, Link2, Link2Off, LoaderCircle, MessageSquarePlus, PencilLine, RefreshCw, Send, Settings2, TriangleAlert } from 'lucide-react';
import { api, streamChat } from '../api.js';
import { useStatus } from '../lib/useStatus.js';
import { applyChatEvent, createDraft } from '../lib/chatState.js';
import { connectedFromState, projectFrom, projectLabel } from '../lib/context.js';
import { consoleUrl } from '../lib/route.js';
import { mcpSummary, interpretMcp } from '../mcp.js';
import { cx, makeTitle, providerLabel } from '../utils.js';
import Thread from './Thread.jsx';
import HistoryDrawer from './HistoryDrawer.jsx';
import { STARTER_PROMPTS } from './prompts.js';

const MODE_KEY = 'tai.mode';

function sortList(list) {
  return [...(list ?? [])].sort((a, b) => String(b.updatedAt).localeCompare(String(a.updatedAt)));
}

/** 工程上下文：来自 GetState / GetProject 的真实结果；未查询时显示未知。 */
function useProjectContext(status, running) {
  const [ctx, setCtx] = useState({ state: 'unknown', project: null, error: '', checkedAt: null });
  const [loading, setLoading] = useState(false);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      const state = await api.tiaState();
      const connected = connectedFromState(state);
      const stateErr = interpretMcp(state).isError ? mcpSummary(state, 160) : '';
      let project = projectFrom(state);
      let projectErr = '';
      if (connected && !project) {
        try {
          const r = await api.callTool('GetProject', {});
          project = projectFrom(r);
          if (interpretMcp(r).isError) projectErr = mcpSummary(r, 160);
        } catch (e) {
          projectErr = e.message;
        }
      }
      setCtx({
        state: connected === true ? 'connected' : connected === false ? 'disconnected' : 'unknown',
        project,
        error: stateErr || projectErr,
        checkedAt: new Date(),
      });
    } catch (e) {
      setCtx((c) => ({ ...c, error: e.message, checkedAt: new Date() }));
    } finally {
      setLoading(false);
    }
  }, []);

  // MCP 已在运行时自动读取一次（不会为此主动启动 MCP）。
  const autoRef = useRef(false);
  useEffect(() => {
    if (autoRef.current || !status || running || status.busy || !status.mcp?.running) return;
    autoRef.current = true;
    refresh();
  }, [status, running, refresh]);

  // 后端连接状态变为未连接时清除旧工程信息。
  useEffect(() => {
    if (status && !status.mcp?.connected) setCtx((c) => (c.state === 'connected' ? { ...c, state: 'disconnected', project: null } : c));
  }, [status]);

  return { ctx, loading, refresh, setCtx };
}

function ContextBar({ status, offline, ctx, loading, running, onRefresh, onConnect, onDisconnect, linking }) {
  const connected = Boolean(status?.mcp?.connected);
  const name = projectLabel(ctx.project);
  const busy = running || loading || linking || status?.busy;
  return (
    <div className="as-context">
      <div className="as-ctx-main">
        <span className={cx('dot', offline ? 'dot-off' : connected ? 'dot-ok' : 'dot-warn')} />
        <div className="as-ctx-text">
          <div className="as-ctx-title" title={ctx.project?.path || ''}>
            {offline ? '本地服务不可用' : connected ? name || '已连接博途 · 工程信息未读取' : status?.mcp?.running ? 'MCP 已启动 · 博途未连接' : '博途未连接'}
          </div>
          <div className="small muted as-ctx-sub" title={ctx.project?.path || ''}>
            {connected && ctx.project?.path ? ctx.project.path : connected ? '工程由 TIA Portal 当前打开的项目决定' : '连接后 TAI 才能读取真实工程'}
          </div>
        </div>
      </div>
      <div className="as-ctx-actions">
        {connected && (
          <button type="button" className="icon-btn" onClick={onRefresh} disabled={busy} title="刷新工程状态" aria-label="刷新工程状态">
            <RefreshCw size={15} className={cx(loading && 'spin')} />
          </button>
        )}
        {connected ? (
          <button type="button" className="btn btn-xs" onClick={onDisconnect} disabled={busy} title="断开 TAI 与 TIA Portal 的 Openness 连接">
            <Link2Off size={13} />
            断开
          </button>
        ) : (
          <button type="button" className="btn btn-xs btn-primary" onClick={onConnect} disabled={busy || Boolean(offline)}>
            {linking ? <LoaderCircle size={13} className="spin" /> : <Link2 size={13} />}
            连接博途
          </button>
        )}
      </div>
    </div>
  );
}

export default function AssistantApp() {
  const { status, offline, refresh: refreshStatus } = useStatus(5000);
  const [conversations, setConversations] = useState([]);
  const [conversation, setConversation] = useState(null);
  const [draft, setDraft] = useState(null);
  const [running, setRunning] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [mode, setMode] = useState(() => (localStorage.getItem(MODE_KEY) === 'write' ? 'write' : 'read'));
  const [notice, setNotice] = useState('');
  const [drawer, setDrawer] = useState(false);
  const [text, setText] = useState('');
  const [linking, setLinking] = useState(false);
  const abortRef = useRef(null);
  const scrollRef = useRef(null);
  const taRef = useRef(null);
  const { ctx, loading: ctxLoading, refresh: refreshCtx } = useProjectContext(status, running);

  useEffect(() => localStorage.setItem(MODE_KEY, mode), [mode]);
  useEffect(() => {
    document.title = 'TAI 工程助手';
  }, []);

  const refreshList = useCallback(async () => {
    try {
      const r = await api.conversations();
      const list = sortList(r?.conversations);
      setConversations(list);
      return list;
    } catch (e) {
      setNotice(e.message);
      return null;
    }
  }, []);

  const openConversation = useCallback(async (id) => {
    setNotice('');
    try {
      setConversation(await api.conversation(id));
    } catch (e) {
      setConversation(null);
      setNotice(e.message);
    }
  }, []);

  useEffect(() => {
    refreshList();
  }, [refreshList]);

  const messages = conversation?.messages ?? [];
  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages.length, draft]);

  function newTask() {
    if (running) return;
    setConversation(null);
    setDraft(null);
    setNotice('');
    setDrawer(false);
    setTimeout(() => taRef.current?.focus(), 0);
  }

  async function deleteConversation(c) {
    if (!window.confirm(`删除任务记录“${c.title || '新任务'}”？此操作不可撤销，不影响博途工程。`)) return;
    try {
      await api.deleteConversation(c.id);
      await refreshList();
      if (conversation?.id === c.id) setConversation(null);
    } catch (e) {
      setNotice(e.message);
    }
  }

  async function send(raw) {
    const message = String(raw ?? '').trim();
    if (!message || running) return false;
    setNotice('');
    let conv = conversation;
    if (!conv) {
      try {
        conv = await api.createConversation(makeTitle(message));
      } catch (e) {
        setNotice(e.message);
        return false;
      }
      setConversation(conv);
    }
    const id = conv.id;
    const now = new Date().toISOString();
    setConversation((c) => ({ ...(c ?? conv), messages: [...((c ?? conv).messages ?? []), { role: 'user', content: message, createdAt: now, mode }] }));
    setDraft(createDraft(now));
    setRunning(true);
    setCancelling(false);
    const controller = new AbortController();
    abortRef.current = controller;
    let failed = '';
    let rejected = false;

    try {
      await streamChat({ conversationId: id, message, mode }, { signal: controller.signal, onEvent: (ev) => setDraft((d) => applyChatEvent(d, ev)) });
    } catch (e) {
      if (e?.name === 'AbortError') failed = '已在本地中止请求。服务端会随连接关闭停止任务；已开始的博途操作可能已经完成，请刷新工程状态。';
      else {
        failed = e.message || String(e);
        if (e.status) rejected = true; // HTTP 错误：服务器未接收该任务
      }
    } finally {
      abortRef.current = null;
      setRunning(false);
      setCancelling(false);
    }

    if (rejected) {
      setDraft(null);
      setNotice(failed);
      setConversation((c) => (c && c.id === id ? { ...c, messages: c.messages.slice(0, -1) } : c));
      refreshStatus();
      refreshList();
      return false;
    }
    try {
      const fresh = await api.conversation(id);
      setConversation((c) => (c && c.id === id ? fresh : c));
      setDraft(null);
      if (failed) setNotice(failed);
    } catch {
      setDraft((d) => (d ? { ...d, status: '', done: true, error: d.error || failed || '' } : d));
    }
    refreshList();
    refreshStatus();
    return true;
  }

  async function cancel() {
    setCancelling(true);
    setDraft((d) => (d ? { ...d, status: '正在取消…' } : d));
    const ctrl = abortRef.current;
    try {
      await api.cancelChat();
    } catch {
      ctrl?.abort();
    }
    // 服务端 8 秒内未结束流时本地中止（关闭连接同样会中止服务端任务）。
    setTimeout(() => {
      if (ctrl && abortRef.current === ctrl) ctrl.abort();
    }, 8000);
  }

  async function link(connect) {
    setLinking(true);
    setNotice('');
    try {
      const r = connect ? await api.tiaConnect() : await api.tiaDisconnect();
      if (interpretMcp(r).isError) setNotice(`${connect ? '连接' : '断开'}失败：${mcpSummary(r, 300)}`);
    } catch (e) {
      setNotice(e.message);
    } finally {
      setLinking(false);
    }
    await refreshStatus();
    if (connect) refreshCtx();
  }

  const submit = async (value) => {
    const ok = await send(value ?? text);
    if (ok && value === undefined) setText('');
  };

  const pickPrompt = (p) => {
    if (p.mode === 'write' && mode !== 'write') setMode('write');
    setText(p.text);
    setTimeout(() => {
      const el = taRef.current;
      if (!el) return;
      el.focus();
      const i = p.text.indexOf('<');
      if (i >= 0) el.setSelectionRange(i, p.text.indexOf('>', i) + 1);
    }, 0);
  };

  const modelReady = Boolean(status?.model?.configured);
  const connected = Boolean(status?.mcp?.connected);
  const empty = !messages.length && !draft;
  const otherBusy = Boolean(status?.busy) && !running;
  const canSend = Boolean(text.trim()) && !running && !offline;
  const href = consoleUrl(window.location.origin);

  return (
    <div className="as-app">
      <header className="as-head">
        <div className="logo">T</div>
        <div className="as-head-title" title={conversation?.title || ''}>
          {conversation?.title || '新任务'}
        </div>
        <button type="button" className="icon-btn" onClick={newTask} disabled={running} title="新任务" aria-label="新任务">
          <MessageSquarePlus size={16} />
        </button>
        <button type="button" className="icon-btn" onClick={() => (refreshList(), setDrawer(true))} title="近期任务" aria-label="近期任务">
          <History size={16} />
        </button>
        <a className="icon-btn" href={href} target="_blank" rel="noopener noreferrer" title="打开配置与诊断控制台（新窗口）" aria-label="打开配置与诊断控制台">
          <Settings2 size={16} />
        </a>
      </header>

      <ContextBar
        status={status}
        offline={offline}
        ctx={ctx}
        loading={ctxLoading}
        running={running}
        linking={linking}
        onRefresh={refreshCtx}
        onConnect={() => link(true)}
        onDisconnect={() => link(false)}
      />

      {offline && (
        <div className="alert alert-err as-banner">
          <TriangleAlert size={15} />
          <span>{offline}</span>
        </div>
      )}
      {!offline && status && !modelReady && (
        <div className="alert alert-warn as-banner">
          <TriangleAlert size={15} />
          <span>
            尚未配置模型，任务无法执行。请在
            <a href={href} target="_blank" rel="noopener noreferrer">
              配置控制台
            </a>
            中填写模型。
          </span>
        </div>
      )}
      {ctx.error && connected && (
        <div className="alert alert-warn as-banner">
          <TriangleAlert size={15} />
          <span>读取工程状态失败：{ctx.error}</span>
        </div>
      )}

      <main className="as-scroll" ref={scrollRef}>
        {empty ? (
          <div className="as-empty">
            <p className="muted small">
              用自然语言描述工程任务。TAI 只能通过 Openness 工具操作 TIA Portal 当前打开的工程，
              {connected ? '执行过程与工具结果会逐步显示。' : '当前未连接博途，只能回答问题或生成代码，不会看到真实工程。'}
            </p>
            <div className="as-prompts">
              {STARTER_PROMPTS.map((p) => (
                <button key={p.title} type="button" className="as-prompt" onClick={() => pickPrompt(p)} disabled={running}>
                  <span className="as-prompt-title">
                    {p.title}
                    {p.mode === 'write' && <span className="tag tag-warn">需写入</span>}
                  </span>
                  <span className="small muted">{p.text}</span>
                </button>
              ))}
            </div>
          </div>
        ) : (
          <Thread messages={messages} draft={draft} onCancel={cancel} cancelling={cancelling} />
        )}
      </main>

      <footer className="as-composer">
        {notice && (
          <div className="alert alert-err">
            <TriangleAlert size={15} />
            <span>{notice}</span>
          </div>
        )}
        {otherBusy && <div className="small warn-text">后端正在执行其他任务（可能来自配置控制台），新任务会被拒绝。</div>}
        <div className={cx('as-box', mode === 'write' && 'as-box-write')}>
          <textarea
            ref={taRef}
            rows={3}
            value={text}
            placeholder="描述工程任务，例如：检查 FB 命名并给出修改建议（Enter 发送，Shift+Enter 换行）"
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                e.preventDefault();
                if (canSend) submit();
              }
            }}
            aria-label="任务描述"
            maxLength={40000}
          />
          <div className="as-box-bar">
            <div className="seg seg-sm" role="radiogroup" aria-label="工作模式">
              <button type="button" role="radio" aria-checked={mode === 'read'} className={cx(mode === 'read' && 'on')} onClick={() => setMode('read')} disabled={running}>
                <Eye size={13} />
                只读
              </button>
              <button type="button" role="radio" aria-checked={mode === 'write'} className={cx(mode === 'write' && 'on on-warn')} onClick={() => setMode('write')} disabled={running}>
                <PencilLine size={13} />
                写入
              </button>
            </div>
            <span className="spacer" />
            {status?.model?.configured && (
              <span className="small muted as-model" title="模型在配置控制台中设置">
                {providerLabel(status.model.provider)} · {status.model.model}
              </span>
            )}
            <button type="button" className="btn btn-sm btn-primary" onClick={() => submit()} disabled={!canSend}>
              {running ? <LoaderCircle size={14} className="spin" /> : <Send size={14} />}
              {running ? '执行中' : '发送'}
            </button>
          </div>
        </div>
      </footer>

      {drawer && (
        <HistoryDrawer
          conversations={conversations}
          activeId={conversation?.id}
          busy={running}
          onClose={() => setDrawer(false)}
          onNew={newTask}
          onDelete={deleteConversation}
          onSelect={(id) => {
            setDrawer(false);
            setDraft(null);
            openConversation(id);
          }}
        />
      )}
    </div>
  );
}
