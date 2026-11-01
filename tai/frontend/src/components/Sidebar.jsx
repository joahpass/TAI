import { MessageSquare, MessageSquarePlus, Settings, Trash2 } from 'lucide-react';
import { cx, dateGroup, formatTime } from '../utils.js';

export default function Sidebar({ conversations, activeId, onSelect, onNew, onDelete, onSettings, busy, status }) {
  const groups = [];
  for (const c of conversations) {
    const g = dateGroup(c.updatedAt);
    let group = groups.find((x) => x.name === g);
    if (!group) groups.push((group = { name: g, items: [] }));
    group.items.push(c);
  }
  return (
    <aside className="sidebar">
      <div className="brand">
        <div className="logo">T</div>
        <div>
          <div className="brand-name">TAI</div>
          <div className="brand-sub">博途工程 AI 助手</div>
        </div>
      </div>
      <button type="button" className="btn btn-primary btn-block" onClick={onNew} disabled={busy}>
        <MessageSquarePlus size={16} />
        新建对话
      </button>
      <nav className="conv-list" aria-label="会话列表">
        {!conversations.length && <div className="muted small pad">暂无会话</div>}
        {groups.map((g) => (
          <div key={g.name}>
            <div className="group-title">{g.name}</div>
            {g.items.map((c) => (
              <div key={c.id} className={cx('conv', c.id === activeId && 'active')}>
                <button type="button" className="conv-main" onClick={() => onSelect(c.id)} title={c.title}>
                  <MessageSquare size={14} />
                  <span className="conv-title">{c.title || '新对话'}</span>
                  <span className="conv-time">{formatTime(c.updatedAt)}</span>
                </button>
                <button
                  type="button"
                  className="icon-btn conv-del"
                  title="删除会话"
                  aria-label="删除会话"
                  disabled={busy}
                  onClick={() => onDelete(c)}
                >
                  <Trash2 size={14} />
                </button>
              </div>
            ))}
          </div>
        ))}
      </nav>
      <div className="side-foot">
        <div className="small muted">
          {status ? `v${status.version}` : '离线'}
          {status?.model?.configured ? ` · ${status.model.model}` : ' · 未配置模型'}
        </div>
        <button type="button" className="btn btn-ghost btn-block" onClick={onSettings}>
          <Settings size={16} />
          设置
        </button>
      </div>
    </aside>
  );
}
