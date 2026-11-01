import { useEffect, useState } from 'react';
import { ExternalLink, LoaderCircle, RefreshCw } from 'lucide-react';
import { api } from '../api.js';
import { releaseLink, updateSummary } from '../lib/updates.js';

export default function UpdateSection() {
  const [data, setData] = useState(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState('');
  useEffect(() => {
    let live = true;
    api.updates().then(value => { if (live) setData(value); })
      .catch(e => { if (live) setError(e.message); })
      .finally(() => { if (live) setBusy(false); });
    return () => { live = false; };
  }, []);
  async function check() {
    setBusy(true);
    setError('');
    try { setData(await api.updates(true)); } catch (e) { setError(e.message); }
    finally { setBusy(false); }
  }
  const href = releaseLink(data);
  return <section className="card cs-card" aria-label="应用更新">
    <div className="row between"><h2>应用更新</h2><span className="small muted">当前版本 {data?.currentVersion || '—'}</span></div>
    <p className={error || data?.status === 'error' ? 'err-text small' : 'small'} role="status" aria-live="polite">{error || updateSummary(data)}</p>
    <div className="row gap cs-actions">
      <button type="button" className="btn btn-sm" disabled={busy} onClick={check}>
        {busy ? <LoaderCircle size={14} className="spin" /> : <RefreshCw size={14} />}检查更新
      </button>
      {href && <a className="btn btn-sm btn-primary" href={href} target="_blank" rel="noopener noreferrer">查看 GitHub Release <ExternalLink size={14} /></a>}
    </div>
    {data?.checkedAt && <div className="hint">上次检查：{new Date(data.checkedAt).toLocaleString()}</div>}
    <div className="hint">仅检查稳定版本。请在 GitHub Release 页面手动下载并安装。</div>
  </section>;
}
