import { useEffect, useState } from 'react';
import { CircleCheck, FlaskConical, KeyRound, LoaderCircle, Save, TriangleAlert, X } from 'lucide-react';
import { api } from '../api.js';
import UpdateSection from './UpdateSection.jsx';

const PRESET_URL = { openai: 'https://api.openai.com/v1', anthropic: 'https://api.anthropic.com' };

export default function Settings({ onClose, onSaved, running }) {
  const [form, setForm] = useState(null);
  const [apiKey, setApiKey] = useState('');
  const [clearKey, setClearKey] = useState(false);
  const [busy, setBusy] = useState('');
  const [msg, setMsg] = useState(null); // {tone, text}
  const [dirty, setDirty] = useState(false);

  useEffect(() => {
    api
      .getConfig()
      .then(setForm)
      .catch((e) => setMsg({ tone: 'err', text: e.message }));
  }, []);

  useEffect(() => {
    const onKey = (e) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const set = (k, v) => {
    setForm((f) => ({ ...f, [k]: v }));
    setDirty(true);
  };

  async function save() {
    setBusy('save');
    setMsg(null);
    try {
      const body = {
        provider: form.provider,
        baseUrl: form.baseUrl,
        model: form.model,
        temperature: Number(form.temperature),
        maxTokens: Number(form.maxTokens),
        mcpPath: form.mcpPath,
        tiaLocation: form.tiaLocation,
        tiaVersion: Number(form.tiaVersion),
      };
      if (clearKey) body.clearApiKey = true;
      else if (apiKey.trim()) body.apiKey = apiKey.trim();
      const next = await api.putConfig(body);
      setForm(next);
      setApiKey('');
      setClearKey(false);
      setDirty(false);
      setMsg({ tone: 'ok', text: '设置已保存。修改 MCP / 博途路径后，MCP 会在下次使用时重新启动。' });
      onSaved?.();
      return true;
    } catch (e) {
      setMsg({ tone: 'err', text: e.message });
      return false;
    } finally {
      setBusy('');
    }
  }

  async function test() {
    if (dirty || apiKey || clearKey) {
      if (!(await save())) return;
    }
    setBusy('test');
    setMsg(null);
    try {
      const r = await api.testModel();
      setMsg({ tone: 'ok', text: `模型响应：${r.text || '（空）'}` });
    } catch (e) {
      setMsg({ tone: 'err', text: e.message });
    } finally {
      setBusy('');
    }
  }

  return (
    <div className="modal-back" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-labelledby="settings-title">
        <div className="modal-head">
          <h2 id="settings-title">设置</h2>
          <button type="button" className="icon-btn" onClick={onClose} aria-label="关闭">
            <X size={18} />
          </button>
        </div>
        {!form ? (
          <div className="modal-body">
            {msg ? <div className="alert alert-err">{msg.text}</div> : <div className="muted">加载中…</div>}
          </div>
        ) : (
          <form
            className="modal-body"
            onSubmit={(e) => {
              e.preventDefault();
              save();
            }}
          >
            <UpdateSection />
            <fieldset>
              <legend>模型</legend>
              <div className="grid2">
                <div className="field">
                  <label htmlFor="s-provider">协议</label>
                  <select
                    id="s-provider"
                    value={form.provider}
                    onChange={(e) => {
                      const p = e.target.value;
                      if (!form.baseUrl || Object.values(PRESET_URL).includes(form.baseUrl)) set('baseUrl', PRESET_URL[p]);
                      set('provider', p);
                    }}
                  >
                    <option value="openai">OpenAI 兼容（/chat/completions）</option>
                    <option value="anthropic">Anthropic（/v1/messages）</option>
                  </select>
                </div>
                <div className="field">
                  <label htmlFor="s-model">模型名称</label>
                  <input id="s-model" value={form.model} onChange={(e) => set('model', e.target.value)} placeholder="例如 gpt-4o / claude-sonnet-4 / deepseek-chat" />
                </div>
              </div>
              <div className="field">
                <label htmlFor="s-url">接口地址</label>
                <input id="s-url" value={form.baseUrl} onChange={(e) => set('baseUrl', e.target.value)} placeholder="https://api.openai.com/v1" />
              </div>
              <div className="field">
                <label htmlFor="s-key">
                  <KeyRound size={13} /> API Key
                  <span className={form.hasApiKey ? 'tag tag-ok' : 'tag tag-warn'}>{form.hasApiKey ? '已保存' : '未设置'}</span>
                </label>
                <input
                  id="s-key"
                  type="password"
                  autoComplete="off"
                  value={apiKey}
                  disabled={clearKey}
                  onChange={(e) => setApiKey(e.target.value)}
                  placeholder={form.hasApiKey ? '留空则保留现有密钥' : '输入 API Key'}
                />
                {form.hasApiKey && (
                  <label className="check small">
                    <input type="checkbox" checked={clearKey} onChange={(e) => setClearKey(e.target.checked)} />
                    <span>清除已保存的密钥</span>
                  </label>
                )}
                <div className="hint">密钥只保存在本机 TAI 数据目录，界面不会回显。</div>
              </div>
              <div className="grid2">
                <div className="field">
                  <label htmlFor="s-temp">温度 (0–2)</label>
                  <input id="s-temp" type="number" min="0" max="2" step="0.1" value={form.temperature} onChange={(e) => set('temperature', e.target.value)} />
                </div>
                <div className="field">
                  <label htmlFor="s-tokens">最大输出 Token</label>
                  <input id="s-tokens" type="number" min="128" max="65536" step="1" value={form.maxTokens} onChange={(e) => set('maxTokens', e.target.value)} />
                </div>
              </div>
            </fieldset>

            <fieldset>
              <legend>博途 / MCP</legend>
              <div className="field">
                <label htmlFor="s-mcp">MCP 可执行文件</label>
                <input id="s-mcp" className="mono" value={form.mcpPath} onChange={(e) => set('mcpPath', e.target.value)} />
              </div>
              <div className="grid2 grid-wide">
                <div className="field">
                  <label htmlFor="s-tia">TIA Portal 安装路径</label>
                  <input id="s-tia" className="mono" value={form.tiaLocation} onChange={(e) => set('tiaLocation', e.target.value)} />
                </div>
                <div className="field">
                  <label htmlFor="s-ver">主版本</label>
                  <select id="s-ver" value={form.tiaVersion} onChange={(e) => set('tiaVersion', e.target.value)}><option value="19">V19</option><option value="21">V21</option></select>
                </div>
              </div>
              <div className="hint">当前 Windows 用户需加入 “Siemens TIA Openness” 用户组。</div>
            </fieldset>

            {msg && (
              <div className={`alert alert-${msg.tone}`}>
                {msg.tone === 'ok' ? <CircleCheck size={15} /> : <TriangleAlert size={15} />}
                <span>{msg.text}</span>
              </div>
            )}
            {running && <div className="small muted">对话执行中，保存与测试需等待任务结束。</div>}

            <div className="modal-foot">
              <button type="button" className="btn" onClick={test} disabled={Boolean(busy) || running}>
                {busy === 'test' ? <LoaderCircle size={15} className="spin" /> : <FlaskConical size={15} />}
                测试模型
              </button>
              <div className="spacer" />
              <button type="button" className="btn btn-ghost" onClick={onClose}>
                关闭
              </button>
              <button type="submit" className="btn btn-primary" disabled={Boolean(busy) || running}>
                {busy === 'save' ? <LoaderCircle size={15} className="spin" /> : <Save size={15} />}
                保存
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
