import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Archive, ArrowUp, ChevronDown, ChevronRight, Circle, ExternalLink, FolderPlus, Link2, Link2Off, LoaderCircle, MoreHorizontal, PanelLeftClose, PanelLeftOpen, PanelRight, Pin, Plus, RefreshCw, Search, Settings2, X } from 'lucide-react';
import Thread from '../assistant/Thread.jsx';
import ToolCall from '../components/ToolCall.jsx';
import { providerLabel, normalizePersistedTool, MODE_INFO } from '../utils.js';
import ModelInput from '../components/ModelInput.jsx';
import { REASONING_OPTIONS, reasoningLabel } from '../lib/taskOptions.js';
import useWorkspace from './useWorkspace.js';
import './workspace.css';

function updated(item) { return Number(new Date(item.updatedAt || item.createdAt || 0)) || 0; }
function recentFirst(a, b) { return Number(Boolean(b.pinned)) - Number(Boolean(a.pinned)) || updated(b) - updated(a); }
function dateLabel(item) { return updated(item) ? new Date(updated(item)).toLocaleDateString('zh-CN', { month: 'numeric', day: 'numeric' }) : ''; }
function IconButton({ label, children, ...props }) { return <button type="button" className="icon-btn" aria-label={label} title={label} {...props}>{children}</button>; }
function EditDialog({ target, ws, onClose }) {
  const dialog = useRef(null), [name, setName] = useState(target.item?.name || target.item?.title || ''), [description, setDescription] = useState(target.item?.description || '');
  const [instructions, setInstructions] = useState(target.item?.instructions || '');
  const [confirmDelete, setConfirmDelete] = useState(false), [deleteName, setDeleteName] = useState(''), [deleting, setDeleting] = useState(false);
  const [projectId, setProjectId] = useState(target.item?.projectId || ws.project?.id || 'default'), [saving, setSaving] = useState(false), [error, setError] = useState('');
  const project = target.kind === 'project', isNew = !target.item;
  const busy = Boolean(ws.preparing || target.item && (project ? ws.task?.projectId === target.item.id || ws.designs.some(d => d.projectId === target.item.id && d.id === ws.task?.conversationId) : ws.task?.conversationId === target.item.id));
  const locked = saving || deleting;
  const conversationCount = project && target.item ? ws.designs.filter(d => d.projectId === target.item.id).length : 0;
  const protectedWorkspace = project && target.item?.id === 'default';
  useEffect(() => { dialog.current.showModal(); }, []);
  async function save(e) {
    e.preventDefault(); if (locked || confirmDelete) return; setSaving(true); setError('');
    const fields = project ? { name: name.trim(), description, instructions } : { title: name.trim(), projectId };
    try {
      const result = isNew ? await ws.createProject(fields) : await ws.mutate(target.kind, target.item, fields);
      if (result) onClose(); else setError('保存未完成，请查看提示，核对最新记录后重试。');
    } catch (err) { setError(err.message || '保存未完成，请重试。'); } finally { setSaving(false); }
  }
  async function archive() {
    setSaving(true); setError('');
    try { const ok = await ws.mutate(target.kind, target.item, { archivedAt: target.item.archivedAt ? null : new Date().toISOString() }); if (ok) onClose(); else setError('归档状态未保存，请查看工作区提示。'); }
    catch (err) { setError(err.message || '归档状态未保存，请重试。'); } finally { setSaving(false); }
  }
  async function remove() {
    if (locked || busy || protectedWorkspace || project && deleteName !== target.item.name) return;
    setDeleting(true); setError('');
    try { const ok = await ws.remove(target.kind, target.item); if (ok) onClose(); else setError('删除未完成，请查看提示后重试。'); }
    catch (err) { setError(err.message || '删除未完成，请重试。'); } finally { setDeleting(false); }
  }
  return <dialog ref={dialog} className="wk-dialog" onCancel={e => { e.preventDefault(); if (!locked) onClose(); }} onClick={e => { if (e.target === dialog.current && !locked) onClose(); }} aria-labelledby="wk-dialog-title">
    <form onSubmit={save}>
      <div className="wk-dialog-head"><h2 id="wk-dialog-title">{isNew ? '新建工作区' : project ? '工作区设置' : '管理对话'}</h2><IconButton label="关闭对话框" disabled={locked} onClick={onClose}><X size={18} /></IconButton></div>
      <fieldset className="wk-dialog-fields" disabled={locked || confirmDelete}>
      <label className="field">{project ? '工作区名称' : '对话名称'}<input autoFocus required maxLength={80} value={name} onChange={e => setName(e.target.value)} /></label>
      {project ? <>
        <label className="field">工作区说明<textarea rows={3} maxLength={2000} value={description} onChange={e => setDescription(e.target.value)} placeholder="简要说明工作区的用途与目标" /></label>
        <label className="field">工作区指令<textarea className="wk-instructions-input" rows={7} maxLength={20000} value={instructions} onChange={e => setInstructions(e.target.value)} placeholder="告诉代理在此工作区中应遵循的开发约定、背景与要求" /><span className="wk-field-hint">应用于此工作区的对话，与工作区说明单独保存。{instructions.length.toLocaleString()} / 20,000</span></label>
      </> : <label className="field">所属工作区<select value={projectId} disabled={busy || saving} onChange={e => setProjectId(e.target.value)}>{ws.projects.map(p => <option key={p.id} value={p.id} disabled={Boolean(p.archivedAt)}>{p.name}{p.archivedAt ? '（已归档）' : ''}</option>)}</select><span className="wk-field-hint">移动后，后续对话使用目标工作区的指令与工程绑定。已归档工作区不可选。</span></label>}
      </fieldset>
      {confirmDelete && <div className="wk-delete-confirm" role="region" aria-label={project ? '确认删除工作区' : '确认删除对话'}>
        <strong>{project ? '删除工作区及所属对话？' : '删除此对话？'}</strong>
        <p>{project ? `将删除此工作区和其中 ${conversationCount} 条对话（含已归档对话）。` : '将删除此对话及其消息和执行记录。'}此操作不可恢复，生成的工件文件会保留。</p>
        {project && <label className="field">输入工作区名称「{target.item.name}」以确认<input aria-label="确认工作区名称" autoComplete="off" value={deleteName} onChange={e => setDeleteName(e.target.value)} disabled={deleting} /></label>}
        <div className="row gap"><button type="button" className="btn" disabled={deleting} onClick={() => { setConfirmDelete(false); setDeleteName(''); setError(''); }}>保留{project ? '工作区' : '对话'}</button><button type="button" className="btn wk-delete" disabled={locked || busy || project && deleteName !== target.item.name} onClick={remove}>{deleting ? '删除中…' : project ? '确认删除工作区' : '确认删除对话'}</button></div>
      </div>}
      {error && <div role="alert" className="alert alert-err">{error}</div>}
      <div className="wk-dialog-foot">{!isNew && <><button type="button" className="btn btn-ghost" disabled={locked || busy || confirmDelete} onClick={archive}><Archive size={14} />{target.item.archivedAt ? '恢复' : '归档'}</button><button type="button" className="btn btn-ghost wk-delete" disabled={locked || busy || protectedWorkspace || confirmDelete} title={protectedWorkspace ? '默认工作区保留' : undefined} onClick={() => { setConfirmDelete(true); setError(''); }}>删除{project ? '工作区' : '对话'}</button></>}<span className="spacer" /><button type="button" className="btn" disabled={locked} onClick={onClose}>取消</button><button className="btn btn-primary" disabled={!name.trim() || locked || confirmDelete}>{saving ? '保存中…' : '保存'}</button></div>
      {protectedWorkspace && <p className="wk-field-hint">默认工作区保留，可归档或管理其中的对话。</p>}
    </form>
  </dialog>;
}
function Sidebar({ ws, mobileOpen, setMobileOpen, archived, setArchived, onManage, searchRef }) {
  const [query, setQuery] = useState(''), [folded, setFolded] = useState({}), [filter, setFilter] = useState('all');
  const term = query.trim().toLowerCase();
  const filtered = ws.designs.filter(d => Boolean(d.archivedAt || ws.projects.find(p => p.id === d.projectId)?.archivedAt) === archived && (filter !== 'pinned' || d.pinned) && (!term || `${d.title || ''} ${ws.projects.find(p => p.id === d.projectId)?.name || ''}`.toLowerCase().includes(term))).sort(filter === 'recent' ? (a, b) => updated(b) - updated(a) : recentFirst);
  const visibleProjects = ws.projects.filter(p => (archived ? p.archivedAt || filtered.some(d => d.projectId === p.id) : !p.archivedAt) && (!term || p.name.toLowerCase().includes(term) || filtered.some(d => d.projectId === p.id)) && (filter === 'all' || filtered.some(d => d.projectId === p.id)));
  function openWorkspace(id) { ws.selectProject(id); setMobileOpen(false); }
  function newConversation(id) { ws.newConversation(id); setMobileOpen(false); }
  return <>
    {mobileOpen && <button className="wk-scrim" aria-label="收起工作区导航" onClick={() => setMobileOpen(false)} />}
    <aside className={`wk-sidebar ${mobileOpen ? 'is-open' : ''}`} aria-label="工作区导航">
      <div className="wk-brand"><strong>TAI</strong><span>代理工作区</span><IconButton label="收起工作区导航" onClick={() => { setMobileOpen(false); ws.setView(v => ({ ...v, sidebar: false })); }}><PanelLeftClose size={16} /></IconButton></div>
      <label className="wk-search"><Search size={14} /><input ref={searchRef} aria-label="搜索工作区与对话" placeholder="搜索工作区与对话" value={query} onChange={e => setQuery(e.target.value)} /><kbd>Ctrl K</kbd></label>
      <div className="wk-actions"><button className="wk-new" onClick={() => newConversation()} disabled={!ws.ready || !ws.project || Boolean(ws.project.archivedAt)}><Plus size={16} />新对话<span>Ctrl N</span></button><button className="wk-nav" onClick={() => onManage({ kind: 'project' })} disabled={!ws.ready}><FolderPlus size={15} />新建工作区</button></div>
      <div className="wk-list-head"><div className="wk-filter" aria-label="工作区归档筛选"><button className={!archived ? 'on' : ''} onClick={() => setArchived(false)}>进行中</button><button className={archived ? 'on' : ''} onClick={() => setArchived(true)}>已归档</button></div><span className="wk-count">{filtered.length}</span></div>
      <div className="wk-list-order wk-filter" aria-label="对话筛选">{[['all', '全部'], ['pinned', '置顶'], ['recent', '最近']].map(([id, label]) => <button key={id} className={filter === id ? 'on' : ''} onClick={() => setFilter(id)}>{label}</button>)}</div>
      <nav className="wk-projects" aria-label="工作区和对话列表">
        {!ws.ready && <div className="wk-list-empty">{ws.offline ? '服务离线，等待重新连接' : '正在读取工作区…'}</div>}
        {visibleProjects.map(p => {
          const list = filtered.filter(d => d.projectId === p.id), selected = ws.project?.id === p.id;
          return <section className="wk-group" key={p.id}>
            <div className={`wk-group-head ${selected ? 'selected' : ''}`}>
              <button className="wk-project-fold" aria-label={`${folded[p.id] ? '展开' : '收起'}工作区 ${p.name}`} onClick={() => setFolded(s => ({ ...s, [p.id]: !s[p.id] }))} aria-expanded={!folded[p.id]}>{folded[p.id] ? <ChevronRight size={13} /> : <ChevronDown size={13} />}</button>
              <button className="wk-project-name" onClick={() => openWorkspace(p.id)} title={p.name} aria-label={`打开工作区 ${p.name}`}><span>{p.name}</span>{p.archivedAt && <Archive size={12} />}</button>
              <IconButton label={`工作区设置 ${p.name}`} onClick={() => onManage({ kind: 'project', item: p })}><MoreHorizontal size={15} /></IconButton>
              {!p.archivedAt && <IconButton label={`在 ${p.name} 中新建对话`} onClick={() => newConversation(p.id)}><Plus size={15} /></IconButton>}
            </div>
            {!folded[p.id] && <div className="wk-designs">{list.map(d => <div className={`wk-design ${ws.view.activeId === d.id ? 'on' : ''}`} key={d.id}>
              <button onClick={() => { ws.open(d.id); setMobileOpen(false); }} title={d.title}><span className={`wk-design-dot ${ws.task?.conversationId === d.id ? 'running' : ''}`}>{d.pinned ? <Pin size={11} /> : ws.task?.conversationId === d.id ? <LoaderCircle size={12} className="spin" /> : <Circle size={6} />}</span><span>{d.title || '新对话'}</span><time>{dateLabel(d)}</time></button>
              <IconButton label={`管理对话 ${d.title}`} onClick={() => onManage({ kind: 'design', item: d })}><MoreHorizontal size={14} /></IconButton>
            </div>)}{!list.length && <div className="wk-list-empty">{term ? '没有匹配对话' : archived ? '暂无归档对话' : '暂无对话，直接输入开始'}</div>}</div>}
          </section>;
        })}
        {ws.ready && !visibleProjects.length && <div className="wk-list-empty">{term || filter !== 'all' ? '没有匹配的工作区或对话' : archived ? '暂无归档工作区或对话' : '创建工作区，开始与代理协作。'}</div>}
      </nav>
      <div className="wk-sidebar-foot"><a className="wk-nav" href="/settings" target="_blank" rel="noreferrer"><Settings2 size={15} />设置与诊断<ExternalLink size={12} /></a><label className="wk-theme"><span>外观</span><select aria-label="外观主题" value={ws.view.theme} onChange={e => ws.setView(v => ({ ...v, theme: e.target.value }))}><option value="light">浅色</option><option value="dark">深色</option><option value="system">跟随系统</option></select></label><div className="wk-profile"><span className={`dot ${ws.offline ? 'dot-off' : 'dot-ok'}`} /><span>{ws.offline ? '本地服务离线' : '本地工作区'}</span></div></div>
    </aside>
  </>;
}
function Details({ ws, onManage, onClose }) {
  const [tab, setTab] = useState('project');
  const tools = [...(ws.design?.messages || []).flatMap(m => m.tools || []), ...(ws.live[ws.view.activeId]?.tools || [])];
  const connected = Boolean(ws.status?.mcp?.connected), busy = Boolean(ws.status?.busy || ws.task || ws.linking);
  const bound = ws.project?.tiaProjectPath || '', actual = ws.context?.project?.path || '', mismatched = Boolean(bound && actual && bound.toLowerCase() !== actual.toLowerCase());
  return <aside className="wk-details" aria-label="工作区上下文与执行记录">
    <div className="wk-details-head"><div className="wk-filter"><button className={tab === 'project' ? 'on' : ''} onClick={() => setTab('project')}>工作区上下文</button><button className={tab === 'tools' ? 'on' : ''} onClick={() => setTab('tools')}>执行记录{tools.length ? ` ${tools.length}` : ''}</button></div><IconButton label="关闭工作区详情" onClick={onClose}><X size={16} /></IconButton></div>
    <div className="wk-details-body">{tab === 'project' ? <>
      <section className="wk-sec"><div className="wk-sec-head">工作区</div><div className="wk-sec-title">{ws.project?.name || '未选择工作区'}</div><p className="wk-sec-desc">{ws.project?.description || '在工作区中组织对话、任务与工程绑定。'}</p>{ws.project && <button className="btn btn-sm" onClick={() => onManage({ kind: 'project', item: ws.project })}><Settings2 size={14} />工作区设置</button>}</section>
      <section className="wk-sec"><div className="wk-sec-head">工作区指令</div><p className="wk-instructions">{ws.project?.instructions || '尚未设置工作区指令。'}</p></section>
      <section className="wk-sec">
        <div className="wk-sec-head">博途工程</div>
        <div className="wk-kv"><span>绑定工程</span><code className={bound ? '' : 'is-empty'}>{bound || '未绑定'}</code></div>
        <div className="wk-kv"><span>连接</span><span className="wk-kv-value"><span className={`dot ${connected ? 'dot-ok' : 'dot-warn'}`} />{connected ? '已连接 TIA Portal' : '未连接博途'}</span></div>
        {connected && <div className="wk-kv"><span>当前打开的工程</span><code className={actual ? '' : 'is-empty'}>{actual || '未读取，点击刷新'}</code></div>}
        {mismatched && <div className="wk-kv-note">实际工程与绑定工程不一致，工程工具会停止执行。</div>}
        <div className="row gap"><button className="btn btn-sm" onClick={ws.bindCurrent} disabled={busy || !connected || !ws.project || Boolean(ws.project.archivedAt)}><Link2 size={14} />绑定当前工程</button>{bound && <button className="btn btn-ghost btn-sm" disabled={busy || Boolean(ws.project?.archivedAt)} onClick={() => ws.mutate('project', ws.project, { tiaProjectPath: null })}>解除绑定</button>}</div>
        <div className="row gap"><button className="btn btn-sm" disabled={busy || Boolean(ws.offline)} onClick={() => ws.connection(connected ? 'disconnect' : 'connect')}>{ws.linking ? <LoaderCircle size={14} className="spin" /> : connected ? <Link2Off size={14} /> : <Link2 size={14} />}{connected ? '断开博途' : '连接博途'}</button><button className="btn btn-sm" disabled={busy || !connected} onClick={() => ws.connection('refresh')}><RefreshCw size={14} />刷新</button>{ws.context?.checkedAt && <span className="small muted">核对 {ws.context.checkedAt}</span>}</div>
        <div className="wk-note">执行工程工具时核对绑定工程与实际工程；切换工作区与标签会保留连接。</div>
      </section>
      {ws.summary && <section className="wk-sec"><div className="wk-sec-head">当前对话</div><div className="wk-sec-title">{ws.summary.title}</div><p className="wk-sec-desc">所属工作区：{ws.projects.find(p => p.id === ws.summary.projectId)?.name || '工作区读取中'}</p><div className="row gap"><button className="btn btn-sm" onClick={() => ws.mutate('design', ws.summary, { pinned: !ws.summary.pinned })}><Pin size={14} />{ws.summary.pinned ? '取消置顶' : '置顶对话'}</button><button className="btn btn-sm" onClick={() => onManage({ kind: 'design', item: ws.summary })}>重命名、移动与归档</button></div></section>}
    </> : tools.length ? tools.map((t, i) => <ToolCall key={t.id || i} tool={t.state ? t : normalizePersistedTool(t)} />) : <div className="wk-list-empty">{ws.view.activeId ? '当前对话还没有工具执行记录。发送任务后，可在这里查看每一步结果。' : '打开对话查看对应的执行记录。'}</div>}</div>
  </aside>;
}
function Home({ ws, onManage }) {
  const project = ws.project;
  if (!project) return <div className="wk-start-panel"><h1>{ws.ready ? '选择一个工作区' : '正在读取工作区…'}</h1><p>在工作区中组织对话，保存上下文与工程绑定。</p>{ws.ready && <button className="btn wk-start" onClick={() => onManage({ kind: 'project' })}><FolderPlus size={15} />新建工作区</button>}</div>;
  const recent = ws.designs.filter(d => d.projectId === project.id && !d.archivedAt).sort((a, b) => updated(b) - updated(a)).slice(0, 8);
  const task = ws.task?.projectId === project.id || ws.designs.some(d => d.id === ws.task?.conversationId && d.projectId === project.id) ? ws.task : null;
  const running = ws.designs.find(d => d.id === task?.conversationId);
  return <div className="wk-start-panel wk-home" data-workspace-id={project.id}>
    <div className="wk-home-heading"><div><span className="wk-eyebrow">工作区首页{project.archivedAt ? ' · 已归档' : ''}</span><h1>{project.name}</h1></div><IconButton label={`工作区设置 ${project.name}`} onClick={() => onManage({ kind: 'project', item: project })}><Settings2 size={17} /></IconButton></div>
    <p className="wk-home-description">{project.description || '在下方输入任务，与代理开始一段新对话。'}</p>
    <button className="wk-home-binding" onClick={() => ws.setView(v => ({ ...v, details: true }))}><Link2 size={14} /><span>{project.tiaProjectPath ? `已绑定 · ${project.tiaProjectPath}` : '尚未绑定博途工程'}</span><ChevronRight size={13} /></button>
    <section className="wk-home-section" aria-label="运行任务"><div className="wk-home-section-head"><h2>运行任务</h2></div>{task ? <div className="wk-home-task"><LoaderCircle size={14} className="spin" /><span>{running?.title || '工作区操作进行中'}</span>{task.conversationId && <button className="btn btn-sm" onClick={() => ws.open(task.conversationId)}>定位任务</button>}{task.kind === 'chat' && <button className="btn btn-sm" disabled={ws.cancelling} onClick={ws.cancel}>{ws.cancelling ? '取消中…' : '取消'}</button>}</div> : <p className="wk-home-empty">当前工作区没有运行中的任务。</p>}</section>
    <section className="wk-home-section" aria-label="最近对话"><div className="wk-home-section-head"><h2>最近对话</h2><span>{recent.length ? `最近 ${recent.length} 条` : ''}</span></div>{recent.length ? <div className="wk-home-conversations">{recent.map(d => <div className="wk-home-conversation" key={d.id}><button onClick={() => ws.open(d.id)}>{d.pinned ? <Pin size={13} /> : <Circle size={6} />}<span>{d.title || '新对话'}</span><time>{dateLabel(d)}</time><ChevronRight size={13} /></button><IconButton label={`管理对话 ${d.title}`} onClick={() => onManage({ kind: 'design', item: d })}><MoreHorizontal size={15} /></IconButton></div>)}</div> : <p className="wk-home-empty">尚无对话。首次发送时会自动创建对话，首页草稿会保留。</p>}</section>
  </div>;
}
export default function WorkspaceApp({ compact = false }) {
  const ws = useWorkspace(compact), [mobileOpen, setMobileOpen] = useState(false), [archived, setArchived] = useState(false), [target, setTarget] = useState(null);
  const scroll = useRef(null), input = useRef(null), searchRef = useRef(null), scrollReady = useRef('');
  const active = ws.view.activeId, home = !active, live = ws.live[active], messages = ws.design?.messages || [], draft = ws.draft || { text: '', mode: 'read' };
  const taskHere = Boolean(active && ws.task?.conversationId === active), archivedDesign = Boolean(ws.summary?.archivedAt || ws.project?.archivedAt), showDetails = ws.view.details;
  const busy = Boolean(ws.task || ws.status?.busy), connected = Boolean(ws.status?.mcp?.connected);
  const sendDisabled = Boolean(ws.preparing || busy || ws.offline || archivedDesign || !ws.ready || !ws.project || (!home && !ws.design) || !draft.text?.trim());
  function send() { if (!sendDisabled) ws.send(); }
  useLayoutEffect(() => {
    const el = scroll.current; if (!el) return;
    if (!active) { scrollReady.current = ''; el.scrollTop = 0; }
    if (scrollReady.current !== active && ws.design) { el.scrollTop = ws.view.positions[active] || 0; scrollReady.current = active; }
  }, [active, ws.design, ws.view.positions, ws.project?.id]);
  useEffect(() => { const el = scroll.current; if (live && el && el.scrollHeight - el.scrollTop - el.clientHeight < 200) el.scrollTop = el.scrollHeight; }, [live]);
  useEffect(() => {
    const onKey = e => {
      if (e.key === 'Escape') { setMobileOpen(false); if (!document.querySelector('dialog[open]')) ws.setView(v => ({ ...v, details: false })); }
      if (!(e.ctrlKey || e.metaKey) || document.querySelector('dialog[open]')) return;
      if (e.key.toLowerCase() === 'k') { e.preventDefault(); ws.setView(v => ({ ...v, sidebar: true })); setMobileOpen(true); setTimeout(() => searchRef.current?.focus(), 30); }
      if (e.key.toLowerCase() === 'n') { e.preventDefault(); if (ws.ready && ws.project && !ws.project.archivedAt) ws.newConversation(); }
      if (e.altKey && ['ArrowLeft', 'ArrowRight'].includes(e.key)) { e.preventDefault(); const tabs = ws.view.tabs, step = e.key === 'ArrowRight' ? 1 : -1; if (tabs.length) ws.open(tabs[(tabs.indexOf(active) + step + tabs.length) % tabs.length]); }
    };
    window.addEventListener('keydown', onKey); return () => window.removeEventListener('keydown', onKey);
  });
  const workspaceUrl = `/workspace${active ? '?design=' + encodeURIComponent(active) : ws.project ? '?project=' + encodeURIComponent(ws.project.id) : ''}`;
  const owner = ws.projects.find(p => p.id === ws.summary?.projectId) || ws.project;
  return <div className={`wk-app ${compact ? 'wk-compact' : ''} ${!ws.view.sidebar ? 'wk-side-hidden' : ''} ${showDetails ? 'wk-with-details' : ''}`}>
    <Sidebar ws={ws} mobileOpen={mobileOpen} setMobileOpen={setMobileOpen} archived={archived} setArchived={setArchived} onManage={setTarget} searchRef={searchRef} />
    <main className="wk-main">
      <header className="wk-header"><IconButton label="展开工作区导航" onClick={() => { ws.setView(v => ({ ...v, sidebar: true })); setMobileOpen(v => !v); }}><PanelLeftOpen size={18} /></IconButton><div className="wk-breadcrumb"><button className="wk-home-link" title={owner?.name || '工作区首页'} aria-label={`返回工作区首页 ${owner?.name || ''}`} onClick={() => ws.showHome(owner?.id)} disabled={!owner}>{owner?.name || '工作区'}</button><ChevronRight size={13} /><strong>{home ? '首页' : ws.summary?.title || '对话记录'}</strong></div><span className="spacer" />{compact && <a href={workspaceUrl} target="_blank" rel="noreferrer" className="icon-btn" title="打开完整工作区" aria-label="打开完整工作区"><ExternalLink size={16} /></a>}<IconButton label="工作区上下文与执行记录" aria-expanded={showDetails} onClick={() => ws.setView(v => ({ ...v, details: !v.details }))}><PanelRight size={18} /></IconButton></header>
      <div className="wk-tabs" role="tablist" aria-label="已打开的对话">{ws.view.tabs.map(id => {
        const d = ws.designs.find(d => d.id === id), isActive = id === active, projectName = ws.projects.find(p => p.id === d?.projectId)?.name || '工作区';
        return <div className={`wk-tab ${isActive ? 'on' : ''}`} key={id}><button id={`tab-${id}`} role="tab" aria-selected={isActive} aria-controls="design-content" tabIndex={isActive || home && id === ws.view.tabs[0] ? 0 : -1} aria-label={`${d?.title || '对话记录'} · ${projectName}`} onClick={() => ws.open(id)} onKeyDown={e => { if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) { e.preventDefault(); const tabs = ws.view.tabs, next = e.key === 'Home' ? tabs[0] : e.key === 'End' ? tabs.at(-1) : tabs[(tabs.indexOf(id) + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length]; ws.open(next); document.getElementById(`tab-${next}`)?.focus(); } }} title={`${d?.title || '对话记录'} · ${projectName}`}>{ws.task?.conversationId === id ? <LoaderCircle size={13} className="spin" /> : <span className={`wk-tab-dot ${ws.view.drafts[id]?.text ? 'has-draft' : ''}`} />}<span className="wk-tab-label"><span>{d?.title || (ws.ready ? '对话记录' : '读取中…')}</span><small>{projectName}</small></span></button><IconButton label={`关闭对话标签 ${d?.title || '对话记录'}`} onClick={() => ws.close(id)}><X size={12} /></IconButton></div>;
      })}<IconButton label="新建对话" onClick={() => ws.newConversation()} disabled={!ws.ready || !ws.project || Boolean(ws.project.archivedAt)}><Plus size={15} /></IconButton></div>
      <div className="wk-context-strip"><span className={`dot ${ws.offline ? 'dot-off' : connected ? 'dot-ok' : 'dot-warn'}`} /><span>{ws.offline ? '本地服务离线' : connected ? '博途已连接' : '博途未连接'}</span><span className="wk-context-path" title={ws.context?.project?.path || ws.project?.tiaProjectPath || ''}>{ws.context?.project?.name || (ws.project?.tiaProjectPath ? ws.project.tiaProjectPath.split(/[\\/]/).pop() : '工程未绑定')}</span><span className="wk-context-mode">{MODE_INFO[draft.mode]?.label || MODE_INFO.read.label}</span><button onClick={() => ws.setView(v => ({ ...v, details: true }))}>查看上下文 <ChevronRight size={12} /></button></div>
      {busy && <div className="wk-task-banner" role="status"><LoaderCircle size={14} className="spin" /><span>{taskHere ? '当前对话正在执行' : `正在执行：${ws.designs.find(d => d.id === ws.task?.conversationId)?.title || '后台操作'}`}</span>{ws.task?.conversationId && !taskHere && <button onClick={() => ws.open(ws.task.conversationId)}>定位任务</button>}{ws.task?.kind === 'chat' && <button disabled={ws.cancelling} onClick={ws.cancel}>{ws.cancelling ? '取消中…' : '取消'}</button>}</div>}
      {ws.notice && <div className="wk-notice" role="alert"><span>{ws.notice}</span><IconButton label="关闭提示" onClick={() => ws.setNotice('')}><X size={14} /></IconButton></div>}
      <section className="wk-content" id="design-content" role={home ? 'region' : 'tabpanel'} aria-label={home ? '工作区首页' : undefined} aria-labelledby={active ? `tab-${active}` : undefined} ref={scroll} onScroll={e => { const top = e.currentTarget.scrollTop; if (active && scrollReady.current === active) ws.setView(v => ({ ...v, positions: { ...v.positions, [active]: top } })); }}>
        {home ? <Home ws={ws} onManage={setTarget} /> : ws.loading[active] && !ws.design ? <div className="wk-loading"><LoaderCircle size={20} className="spin" />读取对话记录…</div> : messages.length || live ? <div className="wk-thread-wrap"><Thread messages={messages} draft={live} onCancel={ws.cancel} cancelling={ws.cancelling} /></div> : <div className="wk-start-panel"><h1>{ws.summary?.title || '开始对话'}</h1><p>在下方描述任务目标，与代理继续协作。</p></div>}
      </section>
      {ws.project && ws.ready && <footer className="wk-composer">
        {archivedDesign && <div className="wk-archived"><Archive size={16} /><span>{ws.project.archivedAt ? '工作区已归档，恢复后继续对话。' : '对话已归档，恢复后继续。'}</span><button className="btn btn-sm" disabled={busy || Boolean(ws.offline)} onClick={() => ws.project.archivedAt ? ws.mutate('project', ws.project, { archivedAt: null }) : ws.mutate('design', ws.summary, { archivedAt: null })}>{ws.project.archivedAt ? '恢复工作区' : '恢复对话'}</button></div>}
        <div className={`as-box wk-compose-box ${draft.mode === 'write' ? 'as-box-write' : ''}`}><textarea ref={input} rows={3} aria-label="对话任务" placeholder={home ? '描述任务，首次发送后创建对话…' : '描述任务，或继续当前对话…'} readOnly={archivedDesign} disabled={Boolean(ws.preparing)} value={draft.text || ''} onChange={e => ws.updateDraft({ text: e.target.value })} onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing && !e.ctrlKey && !e.altKey) { e.preventDefault(); send(); } }} /><div className="wk-compose-bar"><div role="radiogroup" aria-label="执行模式" className="wk-mode">{Object.entries(MODE_INFO).map(([mode, info]) => <button key={mode} type="button" role="radio" disabled={archivedDesign || Boolean(ws.preparing)} aria-checked={draft.mode === mode} title={info.desc} onClick={() => ws.updateDraft({ mode })}>{info.label}</button>)}</div><span className="spacer" /><button className="wk-send" aria-label="发送对话任务" title={ws.preparing ? '正在准备对话' : busy ? '已有任务运行，草稿已保留' : '发送 · Enter'} disabled={sendDisabled} onClick={send}>{ws.preparing ? <LoaderCircle size={17} className="spin" /> : <ArrowUp size={18} />}</button></div></div>
        <div className="wk-task-options" aria-label="下次任务选项">
          <label className="wk-model-field"><span>模型 <small>{providerLabel(ws.models.catalog?.provider || ws.status?.model?.provider)}</small></span><ModelInput id="conversation-model" label="对话模型" value={draft.model} onChange={e => ws.updateDraft({ model: e.target.value })} {...ws.models} disabled={archivedDesign || Boolean(ws.preparing)} placeholder={`跟随配置${ws.models.catalog?.defaultModel || ws.status?.model?.model ? ' · ' + (ws.models.catalog?.defaultModel || ws.status?.model?.model) : ''}`} /></label>
          <label className="wk-effort-field"><span>思考程度</span><select aria-label="思考程度" value={draft.reasoningEffort || ''} title="跟随配置使用已保存默认值；模型默认不附思考参数。服务不支持所选程度时返回错误。" disabled={archivedDesign || Boolean(ws.preparing)} onChange={e => ws.updateDraft({ reasoningEffort: e.target.value })}><option value="">跟随配置 · {reasoningLabel(ws.models.catalog?.reasoningEffort || 'auto')}</option>{REASONING_OPTIONS.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select></label>
        </div>
        <div className="wk-composer-meta"><span>{ws.preparing ? '正在准备对话…' : archivedDesign ? '已归档 · 只读' : busy ? '任务执行中，可切换标签编辑草稿' : 'Enter 发送 · Shift + Enter 换行'}</span><span>{draft.text ? '草稿已保存' : home ? '首次发送时创建对话' : ''}</span></div>
      </footer>}
    </main>
    {showDetails && <Details ws={ws} onManage={setTarget} onClose={() => ws.setView(v => ({ ...v, details: false }))} />}
    {target && <EditDialog key={`${target.kind}-${target.item?.id || 'new'}`} target={target} ws={ws} onClose={() => setTarget(null)} />}
  </div>;
}
