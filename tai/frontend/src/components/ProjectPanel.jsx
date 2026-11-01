import { useEffect, useMemo, useState } from 'react';
import { FolderTree, LoaderCircle, Play, Plug, RefreshCw, TriangleAlert, Unplug, X } from 'lucide-react';
import { api } from '../api.js';
import { interpretMcp } from '../mcp.js';
import { cx, EFFECT_INFO, findTool } from '../utils.js';
import SchemaForm, { initialArgs, missingRequired } from './SchemaForm.jsx';
import { ToolResult } from './ResultView.jsx';

const QUICK = [
  { name: 'GetState', label: '状态' },
  { name: 'GetProject', label: '项目' },
  { name: 'GetProjectTree', label: '项目树' },
  { name: 'GetSoftwareTree', label: '软件树' },
];

function stateConnected(result) {
  const { data } = interpretMcp(result);
  if (data && typeof data === 'object' && !Array.isArray(data)) {
    const v = data.IsConnected ?? data.isConnected;
    if (typeof v === 'boolean') return v;
  }
  return null;
}

export default function ProjectPanel({ status, running, onStatusChange, onClose }) {
  const [tools, setTools] = useState(null);
  const [toolsErr, setToolsErr] = useState('');
  const [loading, setLoading] = useState('');
  const [error, setError] = useState('');
  const [selected, setSelected] = useState('');
  const [args, setArgs] = useState({});
  const [output, setOutput] = useState(null); // {name, result, isError}

  const tool = useMemo(() => tools?.find((t) => t.name === selected) || null, [tools, selected]);
  const connected = Boolean(status?.mcp?.connected);
  const disabled = Boolean(loading) || running;

  async function act(label, fn) {
    setLoading(label);
    setError('');
    try {
      return await fn();
    } catch (e) {
      setError(e.message || String(e));
      return undefined;
    } finally {
      setLoading('');
      onStatusChange?.();
    }
  }

  async function loadTools() {
    setToolsErr('');
    const r = await act('tools', () => api.tools());
    if (r?.tools) setTools(r.tools);
    else setToolsErr('无法获取工具列表（MCP 未能启动？请检查设置中的 MCP 路径）。');
  }

  useEffect(() => {
    if (status?.mcp?.running && !tools && !running) loadTools();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status?.mcp?.running]);

  async function connect() {
    const r = await act('connect', () => api.tiaConnect());
    if (r) setOutput({ seq: Math.random(), name: 'Connect', result: r, isError: Boolean(r.isError) });
    if (r && !tools) loadTools();
  }
  async function disconnect() {
    const r = await act('disconnect', () => api.tiaDisconnect());
    if (r) setOutput({ seq: Math.random(), name: 'Disconnect', result: r, isError: Boolean(r.isError) });
  }
  async function refreshState() {
    const r = await act('state', () => api.tiaState());
    if (r) {
      setOutput({ seq: Math.random(), name: 'GetState', result: r, isError: Boolean(r.isError) });
      if (!tools) loadTools();
    }
  }

  function choose(name) {
    const t = tools?.find((x) => x.name === name);
    setSelected(name);
    setArgs(t ? initialArgs(t.inputSchema) : {});
  }

  async function quick(name) {
    let list = tools;
    if (!list) {
      const r = await act('tools', () => api.tools());
      if (!r?.tools) return;
      list = r.tools;
      setTools(list);
    }
    const t = findTool(list, name);
    if (!t) {
      setError(`MCP 未提供 ${name} 工具`);
      return;
    }
    if ((t.inputSchema?.required ?? []).length) {
      setSelected(t.name);
      setArgs(initialArgs(t.inputSchema));
      setError(`${t.name} 需要参数，请在下方填写后执行。`);
      return;
    }
    await run(t.name, {});
  }

  async function run(name, a) {
    const t = tools?.find((x) => x.name === name);
    if (t?.effect === 'write' && !window.confirm(`${name} 是写入类工具，可能修改博途工程。确认执行？`)) return;
    const r = await act(`call:${name}`, () => api.callTool(name, a));
    if (r) setOutput({ seq: Math.random(), name, result: r, isError: Boolean(r.isError) });
    if (r && name === 'GetState') {
      const c = stateConnected(r);
      if (c !== null) onStatusChange?.();
    }
  }

  const missing = tool ? missingRequired(tool.inputSchema, args) : [];

  return (
    <aside className="panel">
      <div className="panel-head">
        <FolderTree size={16} />
        <span>工程面板</span>
        <button type="button" className="icon-btn" onClick={onClose} title="关闭面板" aria-label="关闭面板">
          <X size={16} />
        </button>
      </div>

      <div className="panel-body">
        <div className="card">
          <div className="row between">
            <div className="conn">
              <span className={cx('dot', connected ? 'dot-ok' : status?.mcp?.running ? 'dot-warn' : 'dot-off')} />
              <div>
                <div className="strong">{connected ? '已连接博途' : '未连接博途'}</div>
                <div className="small muted">
                  MCP {status?.mcp?.running ? `运行中 · ${status.mcp.toolCount} 个工具` : '未启动'}
                </div>
              </div>
            </div>
            <button type="button" className="icon-btn" onClick={refreshState} disabled={disabled} title="刷新状态（启动 MCP 并读取状态）" aria-label="刷新状态">
              <RefreshCw size={15} className={cx(loading === 'state' && 'spin')} />
            </button>
          </div>
          <div className="row gap">
            {connected ? (
              <button type="button" className="btn btn-block" onClick={disconnect} disabled={disabled}>
                {loading === 'disconnect' ? <LoaderCircle size={15} className="spin" /> : <Unplug size={15} />}
                断开
              </button>
            ) : (
              <button type="button" className="btn btn-primary btn-block" onClick={connect} disabled={disabled}>
                {loading === 'connect' ? <LoaderCircle size={15} className="spin" /> : <Plug size={15} />}
                {loading === 'connect' ? '连接中（首次可能需数十秒）…' : '连接博途'}
              </button>
            )}
          </div>
          {running && <div className="small muted">对话执行中，面板操作暂不可用。</div>}
        </div>

        <div className="card">
          <div className="label">快速查看</div>
          <div className="quick">
            {QUICK.map((q) => (
              <button key={q.name} type="button" className="btn btn-sm" onClick={() => quick(q.name)} disabled={disabled}>
                {loading === `call:${q.name}` ? <LoaderCircle size={13} className="spin" /> : null}
                {q.label}
              </button>
            ))}
          </div>
        </div>

        {error && (
          <div className="alert alert-err">
            <TriangleAlert size={15} />
            <span>{error}</span>
          </div>
        )}

        <div className="card">
          <div className="row between">
            <div className="label">MCP 工具</div>
            <button type="button" className="btn btn-ghost btn-xs" onClick={loadTools} disabled={disabled}>
              <RefreshCw size={13} className={cx(loading === 'tools' && 'spin')} />
              {tools ? '刷新' : '加载'}
            </button>
          </div>
          {toolsErr && <div className="small err-text">{toolsErr}</div>}
          {tools && (
            <>
              <select value={selected} onChange={(e) => choose(e.target.value)} aria-label="选择工具">
                <option value="">选择工具…（共 {tools.length} 个）</option>
                {['read', 'lifecycle', 'write'].map((eff) => (
                  <optgroup key={eff} label={EFFECT_INFO[eff].label}>
                    {tools
                      .filter((t) => t.effect === eff)
                      .map((t) => (
                        <option key={t.name} value={t.name}>
                          {t.name}
                        </option>
                      ))}
                  </optgroup>
                ))}
              </select>
              {tool && (
                <div className="tool-form">
                  <div className="row gap">
                    <span className={cx('tag', `tag-${EFFECT_INFO[tool.effect]?.tone || 'info'}`)}>{EFFECT_INFO[tool.effect]?.label}</span>
                    <span className="mono strong">{tool.name}</span>
                  </div>
                  {tool.description && <div className="hint">{tool.description}</div>}
                  <SchemaForm key={tool.name} schema={tool.inputSchema} value={args} onChange={setArgs} />
                  <button
                    type="button"
                    className={cx('btn btn-block', tool.effect === 'write' ? 'btn-warn' : 'btn-primary')}
                    disabled={disabled || missing.length > 0}
                    onClick={() => run(tool.name, args)}
                    title={missing.length ? `缺少必填参数：${missing.join(', ')}` : ''}
                  >
                    {loading === `call:${tool.name}` ? <LoaderCircle size={15} className="spin" /> : <Play size={15} />}
                    执行
                  </button>
                  {missing.length > 0 && <div className="small muted">缺少必填参数：{missing.join(', ')}</div>}
                </div>
              )}
            </>
          )}
        </div>

        {output && (
          <div className="card">
            <div className="row between">
              <div className="label">
                结果 · <span className="mono">{output.name}</span>
              </div>
              <button type="button" className="icon-btn" onClick={() => setOutput(null)} title="清除" aria-label="清除结果">
                <X size={14} />
              </button>
            </div>
            <ToolResult key={output.seq} result={output.result} isError={output.isError} />
          </div>
        )}
      </div>
    </aside>
  );
}
