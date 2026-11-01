import { useState } from 'react';
import { Activity, Link2, Link2Off, LoaderCircle, RefreshCw, TriangleAlert } from 'lucide-react';
import { api } from '../api.js';
import { interpretMcp, mcpSummary } from '../mcp.js';
import { cx, providerLabel } from '../utils.js';
import { ToolResult } from '../components/ResultView.jsx';

function Row({ label, tone, children }) {
  return (
    <div className="cs-kv">
      <span className="muted">{label}</span>
      <span className={cx('cs-kv-v', tone && `${tone}-text`)}>{children}</span>
    </div>
  );
}

/** 服务、模型、MCP 与博途连接状态，以及连接/断开/读取状态操作。 */
export default function StatusSection({ status, offline, onRefresh }) {
  const [act, setAct] = useState('');
  const [out, setOut] = useState(null); // {title, result, error}

  async function run(kind, title, fn) {
    setAct(kind);
    setOut(null);
    try {
      const r = await fn();
      setOut({ title, result: r, isError: interpretMcp(r).isError });
    } catch (e) {
      setOut({ title, error: e.message });
    } finally {
      setAct('');
      onRefresh?.();
    }
  }

  const s = status;
  const busy = Boolean(act) || Boolean(s?.busy);
  return (
    <section className="card cs-card" aria-labelledby="cs-status">
      <div className="row between">
        <h2 id="cs-status">
          <Activity size={16} /> 运行状态
        </h2>
        <button type="button" className="icon-btn" onClick={onRefresh} title="刷新" aria-label="刷新状态">
          <RefreshCw size={15} />
        </button>
      </div>
      {offline ? (
        <div className="alert alert-err">
          <TriangleAlert size={15} />
          <span>{offline}</span>
        </div>
      ) : !s ? (
        <div className="muted small">读取中…</div>
      ) : (
        <div className="cs-kvs">
          <Row label="本地服务">
            {s.app} {s.version}
          </Row>
          <Row label="模型" tone={s.model?.configured ? null : 'warn'}>
            {s.model?.configured ? `${providerLabel(s.model.provider)} · ${s.model.model}` : '未配置模型名称'}
          </Row>
          <Row label="MCP 进程" tone={s.mcp?.running ? 'ok' : null}>
            {s.mcp?.running ? `运行中 · ${s.mcp.toolCount} 个工具` : '未启动'}
          </Row>
          <Row label="博途连接" tone={s.mcp?.connected ? 'ok' : 'warn'}>
            {s.mcp?.connected ? '已连接' : '未连接'}
          </Row>
          <Row label="任务" tone={s.busy ? 'warn' : null}>
            {s.busy ? '有任务正在执行' : '空闲'}
          </Row>
        </div>
      )}
      <div className="row gap cs-actions">
        <button type="button" className="btn btn-sm btn-primary" disabled={busy || Boolean(offline)} onClick={() => run('connect', '连接博途', api.tiaConnect)}>
          {act === 'connect' ? <LoaderCircle size={14} className="spin" /> : <Link2 size={14} />}
          连接博途
        </button>
        <button type="button" className="btn btn-sm" disabled={busy || Boolean(offline)} onClick={() => run('disconnect', '断开博途', api.tiaDisconnect)}>
          {act === 'disconnect' ? <LoaderCircle size={14} className="spin" /> : <Link2Off size={14} />}
          断开
        </button>
        <button type="button" className="btn btn-sm" disabled={busy || Boolean(offline)} onClick={() => run('state', '读取状态 (GetState)', api.tiaState)}>
          {act === 'state' ? <LoaderCircle size={14} className="spin" /> : <RefreshCw size={14} />}
          读取状态
        </button>
      </div>
      <div className="hint">连接、读取状态都会在需要时启动 MCP 进程。连接前请先启动 TIA Portal 并打开工程。</div>
      {out && (
        <div className="cs-out">
          <div className="label">{out.title}</div>
          {out.error ? (
            <div className="alert alert-err">
              <TriangleAlert size={15} />
              <span>{out.error}</span>
            </div>
          ) : (
            <>
              {out.isError && <div className="small err-text">{mcpSummary(out.result, 300)}</div>}
              <ToolResult result={out.result} isError={out.isError} />
            </>
          )}
        </div>
      )}
    </section>
  );
}
