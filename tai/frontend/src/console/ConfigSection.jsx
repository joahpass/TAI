import { useEffect, useMemo, useState } from 'react';
import { CircleCheck, KeyRound, LoaderCircle, RotateCcw, Save, TriangleAlert, Zap } from 'lucide-react';
import { api } from '../api.js';
import { buildConfigBody, changedKeys, endpointPreview, MCP_KEYS, PRESET_URL, toForm, validateForm } from '../lib/configForm.js';
import ModelInput, { useModelCatalog } from '../components/ModelInput.jsx';
import { REASONING_OPTIONS } from '../lib/taskOptions.js';
import { cx } from '../utils.js';

function Field({ label, error, hint, children, wide }) {
  return (
    <label className={cx('field', wide && 'field-wide')}>
      <span className="label">{label}</span>
      {children}
      {error ? <span className="small err-text">{error}</span> : hint ? <span className="hint">{hint}</span> : null}
    </label>
  );
}

/** 模型与 TIA/MCP 配置（GET/PUT /api/config，POST /api/model/test）。 */
export default function ConfigSection({ busy, onSaved }) {
  const models = useModelCatalog();
  const [config, setConfig] = useState(null);
  const [form, setForm] = useState(null);
  const [apiKey, setApiKey] = useState('');
  const [clearKey, setClearKey] = useState(false);
  const [loadErr, setLoadErr] = useState('');
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [msg, setMsg] = useState(null); // {tone,text}
  const [touched, setTouched] = useState(false);

  async function load() {
    setLoadErr('');
    try {
      const c = await api.getConfig();
      setConfig(c); models.configure(c);
      setForm(toForm(c));
      setApiKey('');
      setClearKey(false);
      setTouched(false);
    } catch (e) {
      setLoadErr(e.message);
    }
  }

  useEffect(() => {
    load();
  }, []);

  const errors = useMemo(() => (form ? validateForm(form) : {}), [form]);
  const changed = useMemo(() => (form && config ? changedKeys(form, config) : []), [form, config]);
  const dirty = changed.length > 0 || Boolean(apiKey.trim()) || clearKey;
  const hasErrors = Object.keys(errors).length > 0;
  const mcpChanged = changed.some((k) => MCP_KEYS.includes(k));

  const set = (k) => (e) => {
    setTouched(true);
    setForm((f) => ({ ...f, [k]: e.target.value }));
  };

  function setProvider(p) {
    setTouched(true);
    setForm((f) => {
      const preset = Object.values(PRESET_URL).includes(f.baseUrl.trim()) || !f.baseUrl.trim();
      return { ...f, provider: p, baseUrl: preset ? PRESET_URL[p] : f.baseUrl };
    });
  }

  async function save() {
    setTouched(true);
    if (hasErrors) return null;
    if (mcpChanged && !window.confirm('修改 MCP 路径、TIA 安装路径或版本会停止当前 MCP 进程并断开博途连接。继续保存？')) return null;
    setSaving(true);
    setMsg(null);
    try {
      const c = await api.putConfig(buildConfigBody(form, { apiKey, clearKey }));
      setConfig(c); models.configure(c);
      setForm(toForm(c));
      setApiKey('');
      setClearKey(false);
      setTouched(false);
      setMsg({ tone: 'ok', text: mcpChanged ? '已保存。MCP 已停止，需要时请重新连接博途。' : '已保存。' });
      onSaved?.();
      return c;
    } catch (e) {
      setMsg({ tone: 'err', text: `保存失败：${e.message}` });
      return null;
    } finally {
      setSaving(false);
    }
  }

  async function test() {
    if (dirty) {
      if (!window.confirm('测试使用已保存的配置。先保存当前修改再测试？')) return;
      const ok = await save();
      if (!ok) return;
    }
    setTesting(true);
    setMsg(null);
    try {
      const r = await api.testModel();
      setMsg({ tone: 'ok', text: `模型连接成功。回复：${String(r?.text ?? '').slice(0, 200) || '（空）'}` });
    } catch (e) {
      setMsg({ tone: 'err', text: `模型测试失败：${e.message}` });
    } finally {
      setTesting(false);
    }
  }

  if (loadErr)
    return (
      <section className="card cs-card">
        <h2>模型与连接配置</h2>
        <div className="alert alert-err">
          <TriangleAlert size={15} />
          <span>读取配置失败：{loadErr}</span>
        </div>
        <button type="button" className="btn btn-sm" onClick={load}>
          重试
        </button>
      </section>
    );
  if (!form)
    return (
      <section className="card cs-card">
        <h2>模型与连接配置</h2>
        <div className="muted small">
          <LoaderCircle size={14} className="spin" /> 读取中…
        </div>
      </section>
    );

  const showErr = (k) => (touched ? errors[k] : undefined);

  return (
    <section className="card cs-card" aria-labelledby="cs-config">
      <div className="row between">
        <h2 id="cs-config">模型与连接配置</h2>
        {dirty && <span className="tag tag-warn">有未保存的修改</span>}
      </div>

      <fieldset>
        <legend>模型</legend>
        <div className="cs-grid">
          <Field label="协议" wide>
            <div className="seg seg-sm" role="radiogroup" aria-label="模型协议">
              {[
                ['openai', 'OpenAI 兼容'],
                ['anthropic', 'Anthropic'],
              ].map(([p, l]) => (
                <button key={p} type="button" role="radio" aria-checked={form.provider === p} className={cx(form.provider === p && 'on')} onClick={() => setProvider(p)}>
                  {l}
                </button>
              ))}
            </div>
          </Field>
          <Field label="基础地址" error={showErr('baseUrl')} hint={endpointPreview(form.provider, form.baseUrl) && `请求地址：${endpointPreview(form.provider, form.baseUrl)}`} wide>
            <input value={form.baseUrl} onChange={set('baseUrl')} spellCheck={false} placeholder={PRESET_URL[form.provider]} />
          </Field>
          <Field label="模型名称" hint={!form.model.trim() ? '未填写模型名称时助手无法执行任务' : undefined} wide>
            <ModelInput id="default-model" label="默认模型" value={form.model} onChange={set('model')} {...models} contextChanged={changed.some(k => ['provider', 'baseUrl'].includes(k)) || Boolean(apiKey.trim()) || clearKey} placeholder={form.provider === 'anthropic' ? '例如 claude-sonnet-4-5' : '例如 gpt-4.1'} />
          </Field>
          <Field label="思考程度" error={showErr('reasoningEffort')} hint="模型默认不附思考参数；当前协议或模型不支持所选程度时，服务会返回错误，不会静默降级。" wide>
            <select aria-label="默认思考程度" value={form.reasoningEffort} onChange={set('reasoningEffort')}>{REASONING_OPTIONS.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select>
          </Field>
          <Field label="API Key" wide hint={config.hasApiKey ? '已保存密钥；留空表示保留不变' : '尚未保存密钥'}>
            <div className="row gap">
              <KeyRound size={14} className="muted" />
              <input
                type="password"
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                disabled={clearKey}
                autoComplete="off"
                placeholder={config.hasApiKey ? '••••••••（已保存）' : '输入 API Key'}
              />
            </div>
            {config.hasApiKey && (
              <span className="check">
                <input type="checkbox" checked={clearKey} onChange={(e) => setClearKey(e.target.checked)} />
                清除已保存的密钥
              </span>
            )}
          </Field>
          <Field label="温度" error={showErr('temperature')}>
            <input type="number" step="0.1" min="0" max="2" value={form.temperature} onChange={set('temperature')} />
          </Field>
          <Field label="最大输出 Token" error={showErr('maxTokens')}>
            <input type="number" step="1" min="128" max="65536" value={form.maxTokens} onChange={set('maxTokens')} />
          </Field>
        </div>
      </fieldset>

      <fieldset>
        <legend>TIA Portal / MCP</legend>
        <div className="cs-grid">
          <Field label="MCP 可执行文件" error={showErr('mcpPath')} wide>
            <input value={form.mcpPath} onChange={set('mcpPath')} spellCheck={false} className="mono" />
          </Field>
          <Field label="TIA Portal 安装路径" error={showErr('tiaLocation')} wide>
            <input value={form.tiaLocation} onChange={set('tiaLocation')} spellCheck={false} className="mono" />
          </Field>
          <Field label="TIA 主版本" error={showErr('tiaVersion')} hint="19 表示 V19，21 表示 V21；请与安装路径对应">
            <select value={form.tiaVersion} onChange={set('tiaVersion')}><option value="19">V19</option><option value="21">V21</option></select>
          </Field>
        </div>
        <div className="hint">当前 Windows 用户需加入 “Siemens TIA Openness” 用户组。修改本组设置会停止 MCP 并断开博途。</div>
      </fieldset>

      {msg && (
        <div className={cx('alert', msg.tone === 'ok' ? 'alert-ok' : 'alert-err')}>
          {msg.tone === 'ok' ? <CircleCheck size={15} /> : <TriangleAlert size={15} />}
          <span>{msg.text}</span>
        </div>
      )}
      {busy && <div className="small warn-text">后端正在执行任务，保存或测试会被拒绝（409）。</div>}

      <div className="row gap cs-actions">
        <button type="button" className="btn btn-primary btn-sm" onClick={save} disabled={!dirty || saving || testing || (touched && hasErrors)}>
          {saving ? <LoaderCircle size={14} className="spin" /> : <Save size={14} />}
          保存
        </button>
        <button type="button" className="btn btn-sm" onClick={test} disabled={saving || testing}>
          {testing ? <LoaderCircle size={14} className="spin" /> : <Zap size={14} />}
          测试模型
        </button>
        <button type="button" className="btn btn-ghost btn-sm" onClick={load} disabled={saving || testing || !dirty}>
          <RotateCcw size={14} />
          放弃修改
        </button>
      </div>
    </section>
  );
}
