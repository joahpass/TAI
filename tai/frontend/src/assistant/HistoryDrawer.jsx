import { useEffect, useRef } from 'react';
import { MessageSquarePlus, Trash2, X } from 'lucide-react';
import { cx, dateGroup, formatTime } from '../utils.js';

/** 近期任务抽屉：覆盖在助手之上，不占用常驻宽度。 */
export default function HistoryDrawer({ conversations, activeId, busy, onSelect, onNew, onDelete, onClose }) {
  const ref = useRef(null);

  useEffect(() => {
    ref.current?.focus();
    const onKey = (e) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const groups = [];
  for (const c of conversations) {
    const g = dateGroup(c.updatedAt || c.createdAt);
    const last = groups[groups.length - 1];
    if (last && last.name === g) last.items.push(c);
    else groups.push({ name: g, items: [c] });
  }

  return (
    <div className="as-drawer-wrap" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <aside className="as-drawer" role="dialog" aria-modal="true" aria-label="近期任务" tabIndex={-1} ref={ref}>
        <div className="as-drawer-head">
          <span className="strong">近期任务</span>
          <span className="spacer" />
          <button type="button" className="btn btn-sm" onClick={onNew} disabled={busy}>
            <MessageSquarePlus size={14} />
            新任务
          </button>
          <button type="button" className="icon-btn" onClick={onClose} aria-label="关闭">
            <X size={16} />
          </button>
        </div>
        {busy && <div className="small muted pad">任务执行中，结束后才能切换会话。</div>}
        <div className="as-drawer-list">
          {!conversations.length && <div className="muted small pad">暂无历史任务。</div>}
          {groups.map((g) => (
            <div key={g.name}>
              <div className="as-group">{g.name}</div>
              {g.items.map((c) => (
                <div key={c.id} className={cx('as-conv', c.id === activeId && 'on')}>
                  <button type="button" className="as-conv-main" disabled={busy} onClick={() => onSelect(c.id)} title={c.title}>
                    <span className="as-conv-title">{c.title || '新任务'}</span>
                    <span className="small muted">
                      {formatTime(c.updatedAt || c.createdAt)}
                      {Array.isArray(c.messages) ? ` · ${c.messages.filter((m) => m.role === 'user').length} 条` : ''}
                    </span>
                  </button>
                  <button type="button" className="icon-btn" disabled={busy} onClick={() => onDelete(c)} aria-label={`删除 ${c.title || '新任务'}`}>
                    <Trash2 size={14} />
                  </button>
                </div>
              ))}
            </div>
          ))}
        </div>
      </aside>
    </div>
  );
}
