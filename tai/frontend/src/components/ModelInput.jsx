import { useEffect, useRef, useState } from 'react';
import './ModelInput.css';
import { api } from '../api.js';

// The catalog always belongs to the currently saved service; choosing an ID never changes credentials.
export function useModelCatalog() {
  const [catalog, setCatalog] = useState(null), [loading, setLoading] = useState(false), [error, setError] = useState('');
  const request = useRef(0);
  function configure(config) {
    request.current++; setLoading(false); setError('');
    setCatalog({ models: config.model ? [{ id: config.model }] : [], provider: config.provider, defaultModel: config.model || '', reasoningEffort: config.reasoningEffort || 'auto', source: 'configured' });
  }
  async function load(refresh = false) {
    const id = ++request.current; setLoading(true); setError('');
    try {
      const result = await api.models(refresh);
      if (id === request.current) { setCatalog(result); setError(result?.error || ''); }
    } catch (e) { if (id === request.current) setError(e.message); }
    finally { if (id === request.current) setLoading(false); }
  }
  useEffect(() => {
    const id = ++request.current;
    api.getConfig().then(config => {
      if (id === request.current) setCatalog({ models: config.model ? [{ id: config.model }] : [], provider: config.provider, defaultModel: config.model || '', reasoningEffort: config.reasoningEffort || 'auto', source: 'configured' });
    }).catch(e => { if (id === request.current) setError(e.message); });
    return () => { request.current++; };
  }, []);
  return { catalog, loading, error, load, configure };
}
export default function ModelInput({ id, value, onChange, catalog, loading, error, load, disabled, contextChanged = false, placeholder, label = '模型' }) {
  const items = contextChanged ? [] : catalog?.models || [];
  return <div className="model-picker">
    <div className="model-picker-row"><input id={id} aria-label={label} list={`${id}-list`} value={value || ''} onChange={onChange} disabled={disabled} spellCheck={false} autoComplete="off" placeholder={placeholder || '输入或选择模型 ID'} title="使用当前已保存协议、服务地址与凭据，可手动输入模型 ID" /><button type="button" className="btn btn-xs" aria-label={`加载${label}列表`} title="从当前已保存的模型服务刷新列表" disabled={disabled || loading || contextChanged} onClick={() => load(true)}>{loading ? '加载中…' : '加载列表'}</button></div>
    <datalist id={`${id}-list`}>{items.map(item => <option key={item.id} value={item.id}>{item.label || item.id}</option>)}</datalist>
    {contextChanged ? <span className="hint">列表使用已保存的服务；请先保存协议、地址或凭据修改。可手填模型 ID。</span> : error ? <span className="hint warn-text" role="status">模型列表读取提示：{error}。可手填模型 ID。</span> : catalog?.source === 'configured' ? <span className="hint">尚未加载远程模型列表，可手填模型 ID。</span> : null}
  </div>;
}
