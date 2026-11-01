import { useMemo, useState } from 'react';
import { LoaderCircle, Play, RefreshCw, TriangleAlert, Wrench } from 'lucide-react';
import { api } from '../api.js';
import { interpretMcp, mcpSummary } from '../mcp.js';
import { cx, EFFECT_INFO, fmtDuration, prettyJson } from '../utils.js';
import { needsConfirm, toolLabel } from '../lib/toolLabels.js';
import SchemaForm, { initialArgs, missingRequired } from '../components/SchemaForm.jsx';
import { CopyButton, ToolResult } from '../components/ResultView.jsx';

const ORDER = ['read', 'write', 'lifecycle'];

/** 诊断用手动工具调用（GET /api/tools，POST /api/tools/call）。写入/生命周期工具需确认。 */
export default function ToolTester({ busy, offline, onDone }) {
  const [tools, setTools] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [selected, setSelected] = useState('');
  const [args, setArgs] = useState({});
  const [rawMode, setRawMode] = useState(false);
  const [raw, setRaw] = useState('{}');
  const [running, setRunning] = useState(false);
  const [output, setOutput] = useState(null);
  const [projects, setProjects] = useState([]), [projectId, setProjectId] = useState('');
  const needsProject = tool => tool && !['Connect', 'Disconnect', 'GetState', 'GetCompatibilityInfo'].includes(tool.name);

  async function loadTools() {
    setLoading(true);
    setError('');
    try {
      const [r, p] = await Promise.all([api.tools(), api.projects()]);
      setTools(r?.tools ?? []); setProjects(p.projects || []);
    } catch (e) {
      setError(e.message);
    } finally {
      setLoading(false);
      onDone?.();
    }
  }

  const grouped = useMemo(() => {
    const g = {};
    for (const t of tools ?? []) (g[t.effect] ??= []).push(t);
    for (const k of Object.keys(g)) g[k].sort((a, b) => a.name.localeCompare(b.name));
    return g;
  }, [tools]);

  const tool = tools?.find((t) => t.name === selected) ?? null;

  function choose(name) {
    const t = tools?.find((x) => x.name === name);
    setSelected(name);
    const a = t ? initialArgs(t.inputSchema) : {};
    setArgs(a);
    setRaw(prettyJson(a));
    setOutput(null);
    setError('');
  }

  let parsed = args;
  let rawError = '';
  if (rawMode) {
    try {
      parsed = raw.trim() ? JSON.parse(raw) : {};
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) rawError = '参数必须是 JSON 对象';
    } catch (e) {
      rawError = `JSON 无效：${e.message}`;
    }
  }
  const missing = tool && !rawError ? missingRequired(tool.inputSchema, parsed) : [];

  async function run() {
    if (!tool || rawError) return;
    if (needsConfirm(tool)) {
      const what = tool.effect === 'write' ? '写入类工具，可能修改博途工程' : '连接/工程生命周期工具，会改变 TIA Portal 中打开的工程或连接';
      if (!window.confirm(`${tool.name} 是${what}。确认执行？`)) return;
    }
    setRunning(true);
    setError('');
    setOutput(null);
    const started = Date.now();
    try {
      const r = await api.callTool(tool.name, parsed, projectId);
      setOutput({ name: tool.name, result: r, isError: interpretMcp(r).isError, ms: Date.now() - started });
    } catch (e) {
      setError(e.message);
    } finally {
      setRunning(false);
      onDone?.();
    }
  }

  return (
    <section className="card cs-card" aria-labelledby="cs-tools">
      <div className="row between">
        <h2 id="cs-tools">
          <Wrench size={16} /> 工具调试
        </h2>
        <button type="button" className="btn btn-sm" onClick={loadTools} disabled={loading || Boolean(offline)}>
          {loading ? <LoaderCircle size={14} className="spin" /> : <RefreshCw size={14} />}
          {tools ? '刷新工具列表' : '加载工具列表'}
        </button>
      </div>
      <div className="hint">直接调用 MCP 工具，用于排查连接与工具行为；不经过模型。加载工具列表会在需要时启动 MCP 进程。</div>

      {error && (
        <div className="alert alert-err">
          <TriangleAlert size={15} />
          <span>{error}</span>
        </div>
      )}

      {tools && !tools.length && <div className="muted small">MCP 未返回任何工具。</div>}

      {tools?.length > 0 && (
        <div className="tool-form">
          <label className="field">
            <span className="label">工具（{tools.length}）</span>
            <select value={selected} onChange={(e) => choose(e.target.value)}>
              <option value="">选择工具…</option>
              {ORDER.filter((k) => grouped[k]).map((k) => (
                <optgroup key={k} label={EFFECT_INFO[k]?.label ?? k}>
                  {grouped[k].map((t) => (
                    <option key={t.name} value={t.name}>
                      {t.name} · {toolLabel(t.name)}
                    </option>
                  ))}
                </optgroup>
              ))}
            </select>
          </label>

          {tool && (
            <>
              <div className="row gap">
                <span className={cx('tag', `tag-${EFFECT_INFO[tool.effect]?.tone ?? 'info'}`)}>{EFFECT_INFO[tool.effect]?.label ?? tool.effect}</span>
                <span className="small muted">{tool.description || '无描述'}</span>
              </div>
              {needsProject(tool) && <label className="field"><span className="label">工程上下文</span><select aria-label="调试项目" value={projectId} onChange={e => setProjectId(e.target.value)}><option value="">选择已绑定工程的工作项目…</option>{projects.filter(p => p.tiaProjectPath && !p.archivedAt).map(p => <option key={p.id} value={p.id}>{p.name} · {p.tiaProjectPath}</option>)}</select><span className="hint">在设计工作台的工程上下文中绑定项目；调用前会再次核对实际工程。</span></label>}
              {tool.effect === 'write' && <div className="small warn-text">写入类工具会实际作用于当前博途工程（编译、保存、导入导出、删除/重命名块等），导入、删除、重命名与保存无法由 TAI 自动撤销。</div>}
              <div className="row between">
                <span className="label">参数</span>
                <div className="seg seg-sm">
                  <button type="button" className={cx(!rawMode && 'on')} onClick={() => (setRawMode(false), setArgs(rawError ? args : parsed))}>
                    表单
                  </button>
                  <button type="button" className={cx(rawMode && 'on')} onClick={() => (setRawMode(true), setRaw(prettyJson(args)))}>
                    JSON
                  </button>
                </div>
              </div>
              {rawMode ? (
                <>
                  <textarea className="mono" rows={6} value={raw} onChange={(e) => setRaw(e.target.value)} spellCheck={false} aria-label="JSON 参数" />
                  {rawError && <span className="small err-text">{rawError}</span>}
                </>
              ) : (
                <SchemaForm schema={tool.inputSchema} value={args} onChange={setArgs} />
              )}
              {missing.length > 0 && <span className="small warn-text">缺少必填参数：{missing.join('、')}</span>}
              {busy && !running && <span className="small warn-text">后端正在执行其他任务，调用会被拒绝（409）。</span>}
              <div className="row gap">
                <button
                  type="button"
                  className={cx('btn btn-sm', needsConfirm(tool) ? 'btn-warn' : 'btn-primary')}
                  onClick={run}
                  disabled={running || Boolean(rawError) || missing.length > 0 || Boolean(offline) || (needsProject(tool) && !projectId)}
                >
                  {running ? <LoaderCircle size={14} className="spin" /> : <Play size={14} />}
                  执行 {tool.name}
                </button>
                <details className="small">
                  <summary className="muted">输入 Schema</summary>
                  <pre className="args">{prettyJson(tool.inputSchema ?? {})}</pre>
                </details>
              </div>
            </>
          )}
        </div>
      )}

      {output && (
        <div className="cs-out">
          <div className="row between">
            <span className="label">
              {output.name} · {output.isError ? <span className="err-text">返回错误</span> : <span className="ok-text">完成</span>} · {fmtDuration(output.ms)}
            </span>
            <CopyButton text={prettyJson(output.result)} label="复制结果" />
          </div>
          {output.isError && <div className="small err-text">{mcpSummary(output.result, 300)}</div>}
          <ToolResult result={output.result} isError={output.isError} />
        </div>
      )}
    </section>
  );
}
