import { useMemo, useState } from 'react';
import { Check, Copy } from 'lucide-react';
import { attributeDiagnostics, interpretMcp, resultCompleteness } from '../mcp.js';
import { copyText, cx, prettyJson } from '../utils.js';

export function CopyButton({ text, label = '复制', className }) {
  const [done, setDone] = useState(false);
  return (
    <button
      type="button"
      className={cx('btn btn-ghost btn-xs', className)}
      title={label}
      aria-label={label}
      onClick={async () => {
        if (await copyText(typeof text === 'function' ? text() : text)) {
          setDone(true);
          setTimeout(() => setDone(false), 1500);
        }
      }}
    >
      {done ? <Check size={14} /> : <Copy size={14} />}
      <span>{done ? '已复制' : label}</span>
    </button>
  );
}

function JsonNode({ name, value, depth }) {
  const keyEl = name !== undefined ? <span className="jt-key">{name}: </span> : null;
  if (value === null || typeof value !== 'object') {
    const kind = value === null ? 'null' : typeof value;
    return (
      <div className="jt-row">
        {keyEl}
        <span className={`jt-${kind}`}>{kind === 'string' ? `"${value}"` : String(value)}</span>
      </div>
    );
  }
  const entries = Array.isArray(value) ? value.map((v, i) => [i, v]) : Object.entries(value);
  const label = Array.isArray(value) ? `[${entries.length}]` : `{${entries.length}}`;
  return (
    <details className="jt-node" open={depth < 2}>
      <summary>
        {keyEl}
        <span className="jt-meta">{label}</span>
      </summary>
      <div className="jt-children">
        {entries.slice(0, 500).map(([k, v]) => (
          <JsonNode key={k} name={k} value={v} depth={depth + 1} />
        ))}
        {entries.length > 500 && <div className="jt-meta">… 另有 {entries.length - 500} 项</div>}
      </div>
    </details>
  );
}

export function JsonTree({ value }) {
  return (
    <div className="jtree">
      <JsonNode value={value} depth={0} />
    </div>
  );
}

/** 部分属性读取失败：其余数据仍有效，但不能视为完整结果。 */
function AttributeDiagnostics({ diag }) {
  const shown = diag.failures.slice(0, 50);
  return (
    <div className="attr-diag" role="note">
      <div className="attr-diag-head warn-text">
        部分属性读取失败（{diag.failureCount || shown.length} 项），其余属性已正常返回；结果不完整。
      </div>
      {shown.length > 0 && (
        <ul className="attr-diag-list small">
          {shown.map((f, i) => (
            <li key={i}>
              <span className="mono">{f.owner ? `${f.owner} · ${f.name}` : f.name}</span>
              <span className="tag tag-warn">{f.statusLabel}</span>
              {f.error && <span className="muted mono attr-diag-err">{f.error}</span>}
            </li>
          ))}
          {diag.failures.length > shown.length && <li className="muted">… 另有 {diag.failures.length - shown.length} 项</li>}
        </ul>
      )}
    </div>
  );
}

/** 结果完整性：分页、截断、部分结果、不支持与不可用能力。只显示后端实际返回的字段。 */
function ResultStatus({ status }) {
  const lines = [];
  if (status.unsupported) lines.push('当前目标不支持此读取（后端返回 supported: false）。');
  if (status.partial) lines.push('部分结果：后端标记 partial，部分记录或属性未能读取；其余内容仍有效。');
  else if (status.incomplete) lines.push('未完整读取：后端返回 complete: false，此结果不代表全部内容。');
  if (status.truncated) lines.push('结果已截断，未包含全部内容。');
  const page = [];
  if (status.offset !== null) page.push(`offset ${status.offset}`);
  if (status.returned !== null) page.push(`本页 ${status.returned} 项`);
  if (status.hasMore) page.push('hasMore');
  if (status.nextOffset !== null) page.push(`nextOffset ${status.nextOffset}`);
  if (status.hasMore || status.nextOffset !== null) {
    lines.push(`分页：本页仍有未返回内容${page.length ? `（${page.join('，')}）` : ''}；需要继续分页读取。`);
  } else if (status.offset !== null && status.offset > 0) {
    lines.push(`分页：offset ${status.offset}${status.returned !== null ? `，本页 ${status.returned} 项` : ''}。`);
  }
  if (status.unavailable.length) lines.push(`不可用的能力（${status.unavailable.length} 项）：`);
  return (
    <div className="attr-diag" role="note">
      {lines.map((l, i) => (
        <div key={i} className="attr-diag-head warn-text">{l}</div>
      ))}
      {status.unavailable.length > 0 && (
        <ul className="attr-diag-list small">
          {status.unavailable.slice(0, 30).map((u) => (
            <li key={u.key}>
              <span className="mono">{u.key}</span>
              <span className="tag tag-warn">不可用</span>
              {u.text && <span className="muted attr-diag-err">{u.text}</span>}
            </li>
          ))}
        </ul>
      )}
      {status.diagnostics.length > 0 && (
        <>
          <div className="attr-diag-head warn-text">诊断（{status.diagnosticCount || status.diagnostics.length} 条）：</div>
          <ul className="attr-diag-list small">
            {status.diagnostics.slice(0, 20).map((d, i) => (
              <li key={i}><span className="muted mono attr-diag-err">{d}</span></li>
            ))}
            {status.diagnosticCount > 20 && <li className="muted">… 另有 {status.diagnosticCount - 20} 条</li>}
          </ul>
        </>
      )}
      {status.notes.length > 0 && (
        <ul className="attr-diag-list small">
          {status.notes.map((n, i) => (<li key={i}><span className="muted">{n}</span></li>))}
        </ul>
      )}
    </div>
  );
}

/** MCP 工具结果：结构视图 / 文本 / 原始 JSON */
export function ToolResult({ result, isError }) {
  const info = useMemo(() => interpretMcp(result), [result]);
  const textOut = info.texts.join('\n\n');
  const raw = useMemo(() => prettyJson(result), [result]);
  const diag = useMemo(() => attributeDiagnostics(result), [result]);
  const status = useMemo(() => resultCompleteness(result), [result]);
  const hasData = info.data !== undefined;
  const [view, setView] = useState(hasData ? 'tree' : textOut ? 'text' : 'raw');
  if (result === undefined || result === null) return <div className="muted small">无结果</div>;
  const err = Boolean(isError || info.isError);
  const current = view === 'tree' && !hasData ? 'raw' : view === 'text' && !textOut ? 'raw' : view;
  const copyValue = current === 'tree' ? prettyJson(info.data) : current === 'text' ? textOut : raw;

  return (
    <div className={cx('result', err && 'result-error')}>
      <div className="result-bar">
        <span className={cx('tag', err ? 'tag-err' : 'tag-ok')}>{err ? '返回错误' : '成功'}</span>
        {status.unsupported && <span className="tag tag-warn">不支持</span>}
        {status.partial && <span className="tag tag-warn">部分结果</span>}
        {(status.hasMore || status.nextOffset !== null) && <span className="tag tag-warn">有后续页</span>}
        <div className="seg seg-sm" role="tablist" aria-label="结果视图">
          {hasData && (
            <button type="button" className={cx(current === 'tree' && 'on')} onClick={() => setView('tree')}>
              结构
            </button>
          )}
          {textOut && (
            <button type="button" className={cx(current === 'text' && 'on')} onClick={() => setView('text')}>
              文本
            </button>
          )}
          <button type="button" className={cx(current === 'raw' && 'on')} onClick={() => setView('raw')}>
            原始 JSON
          </button>
        </div>
        <CopyButton text={copyValue} />
      </div>
      {status.notable && <ResultStatus status={status} />}
      {diag.partial && <AttributeDiagnostics diag={diag} />}
      <div className="result-body">
        {current === 'tree' ? <JsonTree value={info.data} /> : <pre>{current === 'text' ? textOut : raw}</pre>}
      </div>
    </div>
  );
}
