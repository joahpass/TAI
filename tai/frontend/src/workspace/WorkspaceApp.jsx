import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Archive, ArrowLeft, ArrowUp, Bot, Check, ChevronDown, ChevronRight, Circle, CircleStop, ExternalLink, FilePlus2, Folder, FolderOpen, FolderPlus, Link2, Link2Off, LoaderCircle, Menu, MoreHorizontal, PanelLeftClose, PanelLeftOpen, PanelRight, Pin, Plus, RefreshCw, Search, Settings2, Sparkles, X } from 'lucide-react';
import Thread from '../assistant/Thread.jsx';
import ToolCall from '../components/ToolCall.jsx';
import { providerLabel, normalizePersistedTool } from '../utils.js';
import { editDraft } from './state.js';
import useWorkspace from './useWorkspace.js';
import './workspace.css';

const IDEAS = [
  { title: '创建控制逻辑', text: '帮我设计一个电机控制 FB，包含启停反馈、故障报警与频率控制。', icon: Sparkles },
  { title: '梳理现有工程', text: '读取当前工程结构，整理 PLC、程序块与 HMI 的设计情况。', icon: FolderOpen },
  { title: '检查程序质量', text: '检查当前工程的程序结构与编译状态，整理问题和改进建议。', icon: Check },
];
function IconButton({ label, children, ...props }) { return <button type="button" className="icon-btn" aria-label={label} title={label} {...props}>{children}</button>; }
function EditDialog({ target, ws, onClose }) {
  const dialog = useRef(null), [name, setName] = useState(target.item?.name || target.item?.title || ''), [description, setDescription] = useState(target.item?.description || '');
  const [projectId, setProjectId] = useState(target.item?.projectId || ws.project?.id || 'default'), [saving, setSaving] = useState(false), [error, setError] = useState('');
  const project = target.kind === 'project', isNew = !target.item;
  const busy = project ? ws.task?.projectId === target.item?.id : ws.task?.conversationId === target.item?.id;
  useEffect(() => { dialog.current.showModal(); }, []);
  async function save(e) {
    e.preventDefault(); setSaving(true); setError('');
    const result = isNew ? await ws.createProject({ name: name.trim(), description }) : await ws.mutate(target.kind, target.item, project ? { name: name.trim(), description } : { title: name.trim(), projectId });
    setSaving(false); if (result) onClose(); else setError('保存未完成，请关闭窗口查看提示，核对最新记录后重试。');
  }
  return <dialog ref={dialog} className="wk-dialog" onCancel={onClose} onClick={e => { if (e.target === dialog.current) onClose(); }}>
    <form onSubmit={save}>
      <div className="wk-dialog-head"><h2>{isNew ? '新建项目' : project ? '管理项目' : '管理设计'}</h2><IconButton label="关闭对话框" onClick={onClose}><X size={18} /></IconButton></div>
      <label className="field">{project ? '项目名称' : '设计名称'}<input autoFocus required maxLength={80} value={name} onChange={e => setName(e.target.value)} /></label>
      {project ? <label className="field">项目说明<textarea rows={3} maxLength={2000} value={description} onChange={e => setDescription(e.target.value)} placeholder="工程目标、开发约定与备注" /></label> :
        <label className="field">所属项目<select value={projectId} disabled={busy} onChange={e => setProjectId(e.target.value)}>{ws.projects.filter(p => !p.archivedAt || p.id === projectId).map(p => <option key={p.id} value={p.id}>{p.name}{p.archivedAt ? '（已归档）' : ''}</option>)}</select></label>}
      {error && <div role="alert" className="alert alert-err">{error}</div>}
      <div className="wk-dialog-foot">
        {!isNew && <button type="button" className="btn btn-ghost" disabled={saving || busy} onClick={async () => { setSaving(true); const ok = await ws.mutate(target.kind, target.item, { archivedAt: target.item.archivedAt ? null : new Date().toISOString() }); setSaving(false); if (ok) onClose(); else setError('归档状态未保存，请查看工作台提示。'); }}><Archive size={14} />{target.item.archivedAt ? '恢复' : '归档'}</button>}
        <span className="spacer" /><button type="button" className="btn" onClick={onClose}>取消</button><button className="btn btn-primary" disabled={!name.trim() || saving}>{saving ? '保存中…' : '保存'}</button>
      </div>
    </form>
  </dialog>;
}
function Sidebar({ ws, mobileOpen, setMobileOpen, archived, setArchived, onManage, searchRef }) {
  const [query, setQuery] = useState(''), [folded, setFolded] = useState({});
  const filtered = ws.designs.filter(d => Boolean(d.archivedAt || ws.projects.find(p => p.id === d.projectId)?.archivedAt) === archived && (!query || d.title?.toLowerCase().includes(query.toLowerCase())));
  const visibleProjects = ws.projects.filter(p => archived ? p.archivedAt || filtered.some(d => d.projectId === p.id) : !p.archivedAt);
  return <>
    {mobileOpen && <button className="wk-scrim" aria-label="收起项目导航" onClick={() => setMobileOpen(false)} />}
    <aside className={`wk-sidebar ${mobileOpen ? 'is-open' : ''}`} aria-label="项目导航">
      <div className="wk-brand"><span className="wk-brand-mark"><Bot size={21} /></span><div><strong>TAI</strong><span>工程设计工作台</span></div><IconButton label="收起项目导航" onClick={() => { setMobileOpen(false); ws.setView(v => ({ ...v, sidebar: false })); }}><PanelLeftClose size={17} /></IconButton></div>
      <label className="wk-search"><Search size={15} /><input ref={searchRef} aria-label="搜索项目与设计" placeholder="搜索项目与设计" value={query} onChange={e => setQuery(e.target.value)} /><kbd>Ctrl K</kbd></label>
      <button className="wk-new" onClick={() => { ws.createDesign(); setMobileOpen(false); }} disabled={!ws.ready}><Plus size={17} />新设计<span>Ctrl N</span></button>
      <button className="wk-nav" onClick={() => onManage({ kind: 'project' })}><FolderPlus size={16} />新建项目</button>
      <div className="wk-list-head"><div className="wk-filter" aria-label="设计筛选"><button className={!archived ? 'on' : ''} onClick={() => setArchived(false)}>进行中</button><button className={archived ? 'on' : ''} onClick={() => setArchived(true)}>已归档</button></div><span>{filtered.length}</span></div>
      <nav className="wk-projects" aria-label="项目和设计列表">
        {!ws.ready && <div className="wk-list-empty">{ws.offline ? '服务离线，等待重新连接' : '正在读取项目…'}</div>}
        {visibleProjects.filter(p => !query || p.name.toLowerCase().includes(query.toLowerCase()) || filtered.some(d => d.projectId === p.id)).map(p => {
          const list = (query && p.name.toLowerCase().includes(query.toLowerCase()) ? ws.designs.filter(d => d.projectId === p.id && Boolean(d.archivedAt || p.archivedAt) === archived) : filtered.filter(d => d.projectId === p.id));
          const selected = ws.project?.id === p.id;
          return <section className="wk-group" key={p.id}>
            <div className={`wk-group-head ${selected ? 'selected' : ''}`}>
              <button className="wk-project-name" onClick={() => { setFolded(s => ({ ...s, [p.id]: !s[p.id] })); ws.selectProject(p.id); }} aria-expanded={!folded[p.id]} title={p.name}>{folded[p.id] ? <ChevronRight size={13} /> : <ChevronDown size={13} />}<Folder size={15} /><span>{p.name}</span></button>
              <IconButton label={`管理项目 ${p.name}`} onClick={() => onManage({ kind: 'project', item: p })}><MoreHorizontal size={15} /></IconButton>
              {!p.archivedAt && <IconButton label={`在 ${p.name} 中新建设计`} onClick={() => { ws.createDesign(p.id); setMobileOpen(false); }}><Plus size={15} /></IconButton>}
            </div>
            {!folded[p.id] && <div className="wk-designs">{list.map(d => <div className={`wk-design ${ws.view.activeId === d.id ? 'on' : ''}`} key={d.id}>
              <button onClick={() => { ws.open(d.id); setMobileOpen(false); }} title={d.title}><span className={`wk-design-dot ${ws.task?.conversationId === d.id ? 'running' : ''}`}>{d.pinned ? <Pin size={11} /> : ws.task?.conversationId === d.id ? <LoaderCircle size={12} className="spin" /> : <Circle size={7} />}</span><span>{d.title || '新设计'}</span><time>{new Date(d.updatedAt).toLocaleDateString('zh-CN', { month: 'numeric', day: 'numeric' })}</time></button>
              <IconButton label={`管理设计 ${d.title}`} onClick={() => onManage({ kind: 'design', item: d })}><MoreHorizontal size={14} /></IconButton>
            </div>)}{!list.length && <div className="wk-list-empty">{archived ? '暂无归档设计' : '暂无设计，点击 + 开始'}</div>}</div>}
          </section>;
        })}
        {ws.ready && !visibleProjects.length && <div className="wk-list-empty">{archived ? '暂无归档项目或设计' : '还没有项目，创建一个开始设计。'}</div>}
      </nav>
      <div className="wk-sidebar-foot"><a className="wk-nav" href="/settings" target="_blank" rel="noreferrer"><Settings2 size={16} />设置与诊断<ExternalLink size={13} /></a><label className="wk-theme"><span>外观</span><select aria-label="外观主题" value={ws.view.theme} onChange={e => ws.setView(v => ({ ...v, theme: e.target.value }))}><option value="light">浅色</option><option value="dark">深色</option><option value="system">跟随系统</option></select></label><div className="wk-profile"><span className={`dot ${ws.offline ? 'dot-off' : 'dot-ok'}`} /><span>{ws.offline ? '本地服务离线' : '本地工作空间'}</span><span className="wk-version">TAI / 01</span></div></div>
    </aside>
  </>;
}
function Details({ ws, onManage, onClose }) {
  const [tab, setTab] = useState('project');
  const tools = [...(ws.design?.messages || []).flatMap(m => m.tools || []), ...(ws.live[ws.view.activeId]?.tools || [])];
  const connected = Boolean(ws.status?.mcp?.connected), busy = Boolean(ws.status?.busy || ws.task || ws.linking);
  return <aside className="wk-details" aria-label="设计详情">
    <div className="wk-details-head"><div className="wk-filter"><button className={tab === 'project' ? 'on' : ''} onClick={() => setTab('project')}>工程上下文</button><button className={tab === 'tools' ? 'on' : ''} onClick={() => setTab('tools')}>执行记录 {tools.length || ''}</button></div><IconButton label="关闭设计详情" onClick={onClose}><X size={16} /></IconButton></div>
    <div className="wk-details-body">{tab === 'project' ? <>
      <div className="wk-detail-caption">当前工作项目</div><div className="wk-detail-title"><Folder size={18} />{ws.project?.name || '选择一个项目'}</div><p className="muted">{ws.project?.description || '将工程目标、设计讨论和执行记录收纳在同一个项目中。'}</p>
      {ws.project && <button className="btn btn-sm" onClick={() => onManage({ kind: 'project', item: ws.project })}><Settings2 size={14} />管理项目</button>}
      <hr /><div className="wk-detail-caption">项目绑定的博途工程</div><div className="wk-path">{ws.project?.tiaProjectPath || '尚未绑定 · 可以先进行设计讨论'}</div>
      <button className="btn btn-sm" onClick={ws.bindCurrent} disabled={busy || !connected || !ws.project || Boolean(ws.project.archivedAt)}><Link2 size={14} />绑定当前工程</button>
      {ws.project?.tiaProjectPath && <button className="btn btn-ghost btn-sm" disabled={busy} onClick={() => ws.mutate('project', ws.project, { tiaProjectPath: null })}>解除绑定</button>}
      <hr /><div className="wk-detail-caption">实际博途连接</div><div className="wk-connection"><span className={`dot ${connected ? 'dot-ok' : 'dot-warn'}`} />{connected ? '已连接 TIA Portal' : '博途未连接'}</div><div className="wk-path">{ws.context?.project?.path || (connected ? '工程信息未读取，点击刷新核对' : '连接后可读取当前工程')}</div>
      <div className="row gap"><button className="btn btn-sm" disabled={busy || Boolean(ws.offline)} onClick={() => ws.connection(connected ? 'disconnect' : 'connect')}>{ws.linking ? <LoaderCircle size={14} className="spin" /> : connected ? <Link2Off size={14} /> : <Link2 size={14} />}{connected ? '断开' : '连接博途'}</button><button className="btn btn-sm" disabled={busy || !connected} onClick={() => ws.connection('refresh')}><RefreshCw size={14} />刷新</button></div>
      {ws.context?.checkedAt && <span className="small muted">核对时间 {ws.context.checkedAt}</span>}
      <div className="wk-note">切换项目和标签保留当前连接。执行工程工具时，会核验绑定路径与实际工程。</div>
      {ws.summary && <><hr /><div className="wk-detail-caption">当前设计</div><div>{ws.summary.title}</div><button className="btn btn-sm" onClick={() => ws.mutate('design', ws.summary, { pinned: !ws.summary.pinned })}><Pin size={14} />{ws.summary.pinned ? '取消置顶' : '置顶设计'}</button><button className="btn btn-sm" onClick={() => onManage({ kind: 'design', item: ws.summary })}>重命名、移动与归档</button></>}
    </> : tools.length ? tools.map((t, i) => <ToolCall key={t.id || i} tool={t.state ? t : normalizePersistedTool(t)} />) : <div className="wk-list-empty">当前设计还没有工具执行记录。发送任务后，可在这里查看每一步结果。</div>}</div>
  </aside>;
}
export default function WorkspaceApp({ compact = false }) {
  const ws = useWorkspace(compact), [mobileOpen, setMobileOpen] = useState(false), [archived, setArchived] = useState(false), [target, setTarget] = useState(null);
  const scroll = useRef(null), input = useRef(null), searchRef = useRef(null), scrollReady = useRef('');
  const active = ws.view.activeId, live = ws.live[active], messages = ws.design?.messages || [];
  const taskHere = ws.task?.conversationId === active, archivedDesign = Boolean(ws.summary?.archivedAt || ws.project?.archivedAt);
  const showDetails = ws.view.details;
  // Restore reading position only after this design has loaded; streaming scrolls only near the end.
  useLayoutEffect(() => {
    const el = scroll.current; if (!el) return;
    if (!active) { scrollReady.current = ''; el.scrollTop = 0; }
    if (scrollReady.current !== active && ws.design) { el.scrollTop = ws.view.positions[active] || 0; scrollReady.current = active; }
  }, [active, ws.design, ws.view.positions]);
  useEffect(() => {
    const el = scroll.current;
    if (live && el && el.scrollHeight - el.scrollTop - el.clientHeight < 200) el.scrollTop = el.scrollHeight;
  }, [live]);
  useEffect(() => {
    const onKey = e => {
      if (e.key === 'Escape') { setMobileOpen(false); if (!document.querySelector('dialog[open]')) ws.setView(v => ({ ...v, details: false })); }
      if (!(e.ctrlKey || e.metaKey) || document.querySelector('dialog[open]')) return;
      if (e.key.toLowerCase() === 'k') { e.preventDefault(); ws.setView(v => ({ ...v, sidebar: true })); setMobileOpen(true); setTimeout(() => searchRef.current?.focus(), 30); }
      if (e.key.toLowerCase() === 'n') { e.preventDefault(); ws.createDesign(); }
      if (e.altKey && ['ArrowLeft', 'ArrowRight'].includes(e.key)) { e.preventDefault(); const tabs = ws.view.tabs, step = e.key === 'ArrowRight' ? 1 : -1; if (tabs.length) ws.open(tabs[(tabs.indexOf(active) + step + tabs.length) % tabs.length]); }
    };
    window.addEventListener('keydown', onKey); return () => window.removeEventListener('keydown', onKey);
  });
  const workspaceUrl = `/workspace${active ? '?design=' + encodeURIComponent(active) : ''}`;
  const busy = Boolean(ws.task || ws.status?.busy);
  function fill(text) { ws.updateDraft({ text }); input.current?.focus(); }
  return <div className={`wk-app ${compact ? 'wk-compact' : ''} ${!ws.view.sidebar ? 'wk-side-hidden' : ''} ${showDetails ? 'wk-with-details' : ''}`}>
    <Sidebar ws={ws} mobileOpen={mobileOpen} setMobileOpen={setMobileOpen} archived={archived} setArchived={setArchived} onManage={setTarget} searchRef={searchRef} />
    <main className="wk-main">
      <header className="wk-header"><IconButton label="展开项目导航" onClick={() => { ws.setView(v => ({ ...v, sidebar: true })); setMobileOpen(v => !v); }}><PanelLeftOpen size={18} /></IconButton><div className="wk-breadcrumb"><span>{ws.project?.name || '设计工作台'}</span><ChevronRight size={13} /><strong>{ws.summary?.title || '开始新的设计'}</strong></div><span className="spacer" />{compact && <a href={workspaceUrl} target="_blank" rel="noreferrer" className="icon-btn" title="打开完整工作台" aria-label="打开完整工作台"><ExternalLink size={16} /></a>}<IconButton label="设计详情" aria-expanded={showDetails} onClick={() => ws.setView(v => ({ ...v, details: !v.details }))}><PanelRight size={18} /></IconButton></header>
      <div className="wk-tabs" role="tablist" aria-label="已打开的设计">{ws.view.tabs.map(id => {
        const d = ws.designs.find(d => d.id === id), isActive = id === active;
        return <div className={`wk-tab ${isActive ? 'on' : ''}`} key={id}><button id={`tab-${id}`} role="tab" aria-selected={isActive} aria-controls="design-content" tabIndex={isActive ? 0 : -1} onClick={() => ws.open(id)} onKeyDown={e => { if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) { e.preventDefault(); const tabs = ws.view.tabs, next = e.key === 'Home' ? tabs[0] : e.key === 'End' ? tabs.at(-1) : tabs[(tabs.indexOf(id) + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length]; ws.open(next); document.getElementById(`tab-${next}`)?.focus(); } }} title={d?.title || '设计记录'}>{ws.task?.conversationId === id ? <LoaderCircle size={13} className="spin" /> : <span className={`wk-tab-dot ${ws.view.drafts[id]?.text ? 'has-draft' : ''}`} />}<span>{d?.title || (ws.ready ? '设计记录' : '读取中…')}</span></button><IconButton label={`关闭标签 ${d?.title || '设计记录'}`} onClick={() => ws.close(id)}><X size={12} /></IconButton></div>;
      })}<IconButton label="新建设计" onClick={() => ws.createDesign()} disabled={!ws.ready}><Plus size={15} /></IconButton></div>
      <div className="wk-context-strip"><span className={`dot ${ws.offline ? 'dot-off' : ws.status?.mcp?.connected ? 'dot-ok' : 'dot-warn'}`} /><span>{ws.offline ? '本地服务离线' : ws.status?.mcp?.connected ? '博途已连接' : '博途未连接'}</span><span className="wk-context-path" title={ws.context?.project?.path || ''}>{ws.context?.project?.name || (ws.project?.tiaProjectPath ? '已绑定工程' : '独立设计')}</span><button onClick={() => ws.setView(v => ({ ...v, details: true }))}>查看上下文 <ChevronRight size={12} /></button></div>
      {busy && <div className="wk-task-banner" role="status"><LoaderCircle size={14} className="spin" /><span>{taskHere ? '当前设计正在执行' : `正在执行：${ws.designs.find(d => d.id === ws.task?.conversationId)?.title || '后台操作'}`}</span>{ws.task?.conversationId && !taskHere && <button onClick={() => ws.open(ws.task.conversationId)}>定位任务</button>}{ws.task?.kind === 'chat' && <button disabled={ws.cancelling} onClick={ws.cancel}>{ws.cancelling ? '取消中…' : '取消'}</button>}</div>}
      {ws.notice && <div className="wk-notice" role="alert"><span>{ws.notice}</span><IconButton label="关闭提示" onClick={() => ws.setNotice('')}><X size={14} /></IconButton></div>}
      <section className="wk-content" id="design-content" role="tabpanel" aria-labelledby={active ? `tab-${active}` : undefined} ref={scroll} onScroll={e => { const top = e.currentTarget.scrollTop; if (active && scrollReady.current === active) ws.setView(v => ({ ...v, positions: { ...v.positions, [active]: top } })); }}>
        {active && (ws.loading[active] && !ws.design) ? <div className="wk-loading"><LoaderCircle size={20} className="spin" />读取设计记录…</div> : messages.length || live ? <div className="wk-thread-wrap"><Thread messages={messages} draft={live} onCancel={ws.cancel} cancelling={ws.cancelling} /></div> : <div className="wk-welcome"><span className="wk-eyebrow">TAI / ENGINEERING WORKSPACE</span><div className="wk-hero-mark"><Bot size={34} strokeWidth={1.5} /></div><h1>{active ? '从一个想法，开始设计。' : '让每一份设计，各就其位。'}</h1><p>{active ? '讨论方案、编写逻辑、检查工程。TAI 与你一起推进每一步。' : '用项目组织工程，用设计记录思路。打开多个任务，自由切换。'}</p>{!active && <button className="btn btn-primary wk-start" onClick={() => ws.createDesign()} disabled={!ws.ready}><Plus size={16} />开始新设计</button>}<div className="wk-ideas">{IDEAS.map(({ title, text, icon: Icon }) => <button key={title} disabled={!ws.ready} onClick={async () => { if (!active) { const d = await ws.createDesign(); if (d) ws.setView(v => editDraft(v, d.id, { text })); } else fill(text); }}><Icon size={18} /><strong>{title}</strong><span>{text}</span><span className="wk-idea-arrow">↗</span></button>)}</div><div className="wk-welcome-note"><span className="wk-tiny-dot" />{ws.ready ? `${ws.projects.filter(p => !p.archivedAt).length} 个项目 · ${ws.designs.filter(d => !d.archivedAt).length} 份设计 · 草稿自动保存` : '正在准备你的工作空间'}</div></div>}
      </section>
      {active && <footer className="wk-composer">
        {archivedDesign ? <div className="wk-archived"><Archive size={16} /><span>{ws.project?.archivedAt ? '项目已归档，在项目管理中恢复后继续。' : '设计已归档，恢复后可以继续编辑。'}</span>{!ws.project?.archivedAt && <button className="btn btn-sm" onClick={() => ws.mutate('design', ws.summary, { archivedAt: null })}>恢复设计</button>}</div> : <>
          <div className={`as-box wk-compose-box ${ws.draft.mode === 'write' ? 'as-box-write' : ''}`}><textarea ref={input} rows={3} aria-label="设计任务" placeholder="描述你的设计目标，或继续完善当前方案…" value={ws.draft.text || ''} onChange={e => ws.updateDraft({ text: e.target.value })} onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing && !e.ctrlKey && !e.altKey) { e.preventDefault(); ws.send(); } }} /><div className="wk-compose-bar"><div role="radiogroup" aria-label="执行模式" className="wk-mode"><button role="radio" aria-checked={ws.draft.mode !== 'write'} onClick={() => ws.updateDraft({ mode: 'read' })}>只读</button><button role="radio" aria-checked={ws.draft.mode === 'write'} onClick={() => ws.updateDraft({ mode: 'write' })}>写入</button></div><span className="wk-model" title={ws.status?.model?.model || ''}>{ws.status?.model?.configured ? `${providerLabel(ws.status.model.provider)} / ${ws.status.model.model}` : '配置模型后开始'}</span><span className="spacer" /><button className="wk-send" aria-label="发送设计任务" title={busy ? '已有任务运行，草稿已保留' : '发送 · Enter'} disabled={busy || !ws.draft.text?.trim() || !ws.design || Boolean(ws.offline)} onClick={ws.send}><ArrowUp size={19} /></button></div></div>
          <div className="wk-composer-meta"><span>{busy ? '任务串行执行 · 切换标签可继续编辑草稿' : 'Enter 发送 · Shift Enter 换行'}</span><span>{ws.draft.text ? '草稿已保存' : 'TAI 工程助手'}</span></div>
        </>}
      </footer>}
      {!active && <footer className="wk-idle-foot"><span>项目有序，设计专注。</span><a href="/settings" target="_blank" rel="noreferrer">模型与连接设置 <ExternalLink size={12} /></a></footer>}
    </main>
    {showDetails && <Details ws={ws} onManage={setTarget} onClose={() => ws.setView(v => ({ ...v, details: false }))} />}
    {target && <EditDialog key={`${target.kind}-${target.item?.id || 'new'}`} target={target} ws={ws} onClose={() => setTarget(null)} />}
  </div>;
}
