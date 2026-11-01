import { useCallback, useEffect, useRef, useState } from 'react';
import { api, streamChat } from '../api.js';
import { useStatus } from '../lib/useStatus.js';
import { applyChatEvent, createDraft } from '../lib/chatState.js';
import { connectedFromState, projectFrom } from '../lib/context.js';
import { interpretMcp, mcpSummary } from '../mcp.js';
import { closeTab, editDraft, openTab, restoreView, sortedDesigns } from './state.js';

function windowKey(compact) {
  const kind = compact ? 'assistant' : 'workspace';
  let id = sessionStorage.getItem(`tai.${kind}.window`);
  if (!id) { id = crypto.randomUUID(); sessionStorage.setItem(`tai.${kind}.window`, id); }
  return `tai.${kind}.${id}`;
}
export default function useWorkspace(compact) {
  const entry = useRef(new URLSearchParams(window.location.search));
  const [storage] = useState(() => { try { return windowKey(compact); } catch { return ''; } });
  const [view, setView] = useState(() => {
    try { return restoreView(JSON.parse(localStorage.getItem(storage) || localStorage.getItem(`tai.last.${compact ? 'assistant' : 'workspace'}`) || 'null')); }
    catch { return restoreView(null); }
  });
  const [projects, setProjects] = useState([]), [designs, setDesigns] = useState([]), [records, setRecords] = useState({});
  const [notice, setNotice] = useState(''), [ready, setReady] = useState(false), [loading, setLoading] = useState({});
  const [live, setLive] = useState({}), [localTask, setLocalTask] = useState(null), [cancelling, setCancelling] = useState(false);
  const [context, setContext] = useState(null), [linking, setLinking] = useState(false);
  const { status, offline, refresh: refreshStatus } = useStatus(4000);
  const viewRef = useRef(view), recordsRef = useRef(records), taskRef = useRef(null), sequence = useRef(0), loads = useRef({}), initialLink = useRef(false);
  viewRef.current = view; recordsRef.current = records;
  useEffect(() => {
    try {
      localStorage.setItem(storage, JSON.stringify(view));
      localStorage.setItem(`tai.last.${compact ? 'assistant' : 'workspace'}`, JSON.stringify(view));
    } catch { setNotice('浏览器存储已满或不可用，草稿暂存在当前窗口，请保持窗口打开。'); }
  }, [storage, view, compact]);
  useEffect(() => {
    try { localStorage.setItem('tai.theme', view.theme); } catch { /* Draft persistence reports storage errors. */ }
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const apply = () => document.documentElement.dataset.theme = view.theme === 'system' ? (media.matches ? 'dark' : 'light') : view.theme;
    apply(); media.addEventListener('change', apply);
    return () => media.removeEventListener('change', apply);
  }, [view.theme]);
  useEffect(() => {
    document.title = compact ? 'TAI 工程助手' : 'TAI 设计工作台';
    return () => taskRef.current?.controller.abort();
  }, [compact]);

  const loadDesign = useCallback(async (id) => {
    if (!id) return;
    const request = loads.current[id] = (loads.current[id] || 0) + 1;
    setLoading(s => ({ ...s, [id]: true }));
    try {
      const item = await api.conversation(id);
      if (loads.current[id] === request && taskRef.current?.conversationId !== id) setRecords(s => (s[id]?.revision || 0) > item.revision ? s : { ...s, [id]: item });
      return item;
    } catch (e) { if (loads.current[id] === request) setNotice(e.message); }
    finally { if (loads.current[id] === request) setLoading(s => ({ ...s, [id]: false })); }
  }, []);
  const refresh = useCallback(async () => {
    const requestId = ++sequence.current;
    try {
      const [p, c] = await Promise.all([api.projects(), api.conversations({ summary: '1', archived: 'all' })]);
      if (requestId !== sequence.current) return;
      setProjects(p.projects || []); setDesigns(sortedDesigns(c.conversations || [])); setReady(true);
      return { projects: p.projects || [], designs: c.conversations || [] };
    } catch (e) { setNotice(e.message); return null; }
  }, []);
  useEffect(() => {
    refresh();
    const tick = setInterval(() => { refresh(); const id = viewRef.current.activeId; if (id && !taskRef.current) loadDesign(id); }, 6500);
    const onFocus = () => refresh();
    window.addEventListener('focus', onFocus);
    return () => { clearInterval(tick); window.removeEventListener('focus', onFocus); };
  }, [refresh, loadDesign]);
  useEffect(() => {
    if (!ready || initialLink.current) return;
    initialLink.current = true;
    const id = entry.current.get('design');
    const pid = entry.current.get('project');
    if (id || pid) setView(v => ({ ...(id ? openTab(v, id) : { ...v, activeId: '' }), ...(pid ? { projectId: pid } : {}) }));
  }, [ready]);
  useEffect(() => {
    if (!view.activeId) return;
    loadDesign(view.activeId);
    const url = new URL(window.location.href); url.searchParams.set('design', view.activeId);
    window.history.replaceState(null, '', url);
  }, [view.activeId, loadDesign]);
  useEffect(() => {
    const onPop = () => { const id = new URLSearchParams(window.location.search).get('design'); if (id) setView(v => openTab(v, id)); };
    window.addEventListener('popstate', onPop); return () => window.removeEventListener('popstate', onPop);
  }, []);

  const task = localTask || status?.activeTask;
  const design = records[view.activeId], summary = designs.find(d => d.id === view.activeId);
  const project = projects.find(p => p.id === (summary?.projectId || design?.projectId || view.projectId));
  function selectProject(id) {
    setView(v => ({ ...v, projectId: id, activeId: '' }));
    const url = new URL(location.href); url.searchParams.delete('design'); url.searchParams.set('project', id); history.replaceState(null, '', url);
  }
  function open(id) { setView(v => openTab(v, id)); }
  function close(id) {
    setView(v => { const next = closeTab(v, id); if (!next.activeId) { const url = new URL(location.href); url.searchParams.delete('design'); history.replaceState(null, '', url); } return next; });
  }
  async function createDesign(projectId = project?.id || view.projectId) {
    try {
      const p = projects.find(p => p.id === projectId && !p.archivedAt) || projects.find(p => !p.archivedAt);
      if (!p) { setNotice('先创建或恢复一个项目，再新建设计。'); return; }
      const item = await api.createConversation('新设计', p.id);
      setRecords(s => ({ ...s, [item.id]: item }));
      setView(v => ({ ...openTab(v, item.id), projectId: p.id }));
      await refresh(); return item;
    } catch (e) { setNotice(e.message); }
  }
  async function mutate(kind, item, changes) {
    try {
      const result = kind === 'project' ? await api.updateProject(item.id, { ...changes, revision: item.revision }) : await api.updateConversation(item.id, { ...changes, revision: item.revision });
      if (kind === 'design') setRecords(s => s[item.id] ? { ...s, [item.id]: { ...result, ...(taskRef.current?.conversationId === item.id ? { messages: s[item.id].messages } : {}) } } : s);
      await refresh(); return true;
    } catch (e) { setNotice(e.status === 409 ? `${e.message} 已重新读取记录，请检查后重试。` : e.message); await refresh(); return false; }
  }
  async function createProject(data) {
    try { const p = await api.createProject(data); selectProject(p.id); await refresh(); return p; }
    catch (e) { setNotice(e.message); }
  }
  async function readContext() {
    const state = await api.tiaState();
    if (interpretMcp(state).isError) throw new Error(mcpSummary(state, 200));
    const actual = projectFrom(state);
    const ctx = { connected: connectedFromState(state), project: actual, checkedAt: new Date().toLocaleTimeString() };
    setContext(ctx); return ctx;
  }
  async function connection(action) {
    setLinking(true);
    try {
      if (action !== 'refresh') {
        const r = action === 'connect' ? await api.tiaConnect() : await api.tiaDisconnect();
        if (interpretMcp(r).isError) throw new Error(mcpSummary(r, 200));
      }
      if (action === 'disconnect') setContext(null); else await readContext();
    } catch (e) { setNotice(e.message); }
    finally { setLinking(false); refreshStatus(); }
  }
  async function bindCurrent() {
    if (!project) return;
    setLinking(true);
    try {
      const ctx = await readContext();
      if (!ctx.connected || !ctx.project?.path) throw new Error('请先在博途中打开工程并连接，再绑定当前工程。');
      await mutate('project', project, { tiaProjectPath: ctx.project.path });
    } catch (e) { setNotice(e.message); }
    finally { setLinking(false); refreshStatus(); }
  }
  // Reading status never starts a bridge. Real context is requested by the user, or when an already-running bridge first connects.
  const autoContext = useRef(false);
  useEffect(() => {
    if (!autoContext.current && status?.mcp?.running && status.mcp.connected && !status.busy) {
      autoContext.current = true; readContext().catch(e => setNotice(e.message));
    }
    if (status && !status.mcp?.connected) setContext(null);
  }, [status]);

  async function send() {
    const id = viewRef.current.activeId, savedDraft = viewRef.current.drafts[id] || {}, text = (savedDraft.text || '').trim();
    if (!id || !text || taskRef.current) return;
    if (status?.busy) { setNotice('已有任务执行中，可定位任务查看进度；当前草稿已保留。'); return; }
    const before = recordsRef.current[id];
    if (!before) return;
    const mode = savedDraft.mode || 'read', now = new Date().toISOString(), controller = new AbortController();
    const run = { conversationId: id, projectId: before.projectId, controller, kind: 'chat', taskId: '' };
    taskRef.current = run; setLocalTask(run); setNotice('');
    setRecords(s => ({ ...s, [id]: { ...s[id], messages: [...s[id].messages, { role: 'user', content: text, mode, createdAt: now }] } }));
    setLive(s => ({ ...s, [id]: createDraft(now) }));
    setView(v => editDraft(v, id, { text: '' }));
    let failed = '', rejected = false;
    try {
      await streamChat({ conversationId: id, message: text, mode }, { signal: controller.signal, onEvent: ev => {
        if (ev.taskId) { run.taskId = ev.taskId; setLocalTask({ ...run }); }
        setLive(s => ({ ...s, [id]: applyChatEvent(s[id], ev) }));
      } });
    } catch (e) { failed = e.name === 'AbortError' ? '连接已中断，任务停止；重新读取记录以核对实际结果。' : e.message; rejected = Boolean(e.status); }
    finally {
      taskRef.current = null; setLocalTask(null); setCancelling(false);
      if (rejected) {
        setRecords(s => ({ ...s, [id]: before }));
        setView(v => editDraft(v, id, { text: v.drafts[id]?.text ? `${text}\n\n${v.drafts[id].text}` : text }));
      }
      try {
        const item = await api.conversation(id); setRecords(s => ({ ...s, [id]: item }));
        setLive(s => { const next = { ...s }; delete next[id]; return next; });
      } catch {
        setLive(s => ({ ...s, [id]: { ...s[id], done: true, status: '', error: failed || '读取最新记录失败，请刷新重试。' } }));
      }
      if (failed) setNotice(failed);
      refresh(); refreshStatus();
    }
  }
  async function cancel() {
    const current = taskRef.current;
    setCancelling(true);
    try {
      const active = (await api.status()).activeTask;
      const expected = current?.taskId || task?.taskId;
      if (!active || active.kind !== 'chat' || (expected && active.taskId !== expected) || active.conversationId !== (current?.conversationId || task?.conversationId)) return;
      await api.cancelChat(active.taskId);
    } catch (e) { setNotice(e.message); }
    finally { setCancelling(false); refreshStatus(); }
  }
  return { view, setView, projects, designs, records, design, summary, project, notice, setNotice, ready, loading, live, task, status, offline,
    context, linking, cancelling, open, close, selectProject, createDesign, createProject, mutate, refresh, send, cancel, connection, bindCurrent,
    draft: view.drafts[view.activeId] || { text: '', mode: 'read' }, updateDraft: change => setView(v => editDraft(v, v.activeId, change)) };
}
