import { useEffect, useMemo, useRef, useState } from 'react';
import { CircleCheck, Download, FileUp, LoaderCircle, RotateCcw, Save, TriangleAlert, Undo2 } from 'lucide-react';
import { api } from '../api.js';
import {
  buildPromptBody,
  effectivePrompt,
  exportPayload,
  IMPORT_ACCEPT,
  isOverride,
  isPromptDirty,
  MAX_PROMPT_CHARS,
  normalizePrompts,
  readPromptFile,
  validatePromptDraft,
} from '../lib/promptEditor.js';
import { cx } from '../utils.js';

function download({ fileName, mime, content }) {
  const url = URL.createObjectURL(new Blob([content], { type: mime }));
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

/** 助手系统提示词（GET/PUT /api/prompts）。导入只暂存为草稿，需手动保存。 */
export default function PromptSection({ busy }) {
  const [prompts, setPrompts] = useState(null);
  const [draft, setDraft] = useState('');
  const [loadErr, setLoadErr] = useState('');
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState(null); // {tone,text}
  const fileRef = useRef(null);

  async function load() {
    setLoadErr('');
    try {
      const p = normalizePrompts(await api.getPrompts());
      setPrompts(p);
      setDraft(effectivePrompt(p));
    } catch (e) {
      setLoadErr(e.message);
    }
  }

  useEffect(() => {
    load();
  }, []);

  const dirty = useMemo(() => (prompts ? isPromptDirty(draft, prompts) : false), [draft, prompts]);
  const error = validatePromptDraft(draft);
  const overridden = prompts ? isOverride(prompts) : false;

  // 有未保存草稿时离开页面需确认。
  useEffect(() => {
    if (!dirty) return undefined;
    const onBefore = (e) => {
      e.preventDefault();
      e.returnValue = '';
    };
    window.addEventListener('beforeunload', onBefore);
    return () => window.removeEventListener('beforeunload', onBefore);
  }, [dirty]);

  async function put(text, okText) {
    setSaving(true);
    setMsg(null);
    try {
      const p = normalizePrompts(await api.putPrompts(text));
      setPrompts(p);
      setDraft(effectivePrompt(p));
      setMsg({ tone: 'ok', text: okText });
    } catch (e) {
      // 保存失败：保留草稿，便于重试或导出。
      setMsg({ tone: 'err', text: `保存失败：${e.message}。草稿已保留，未写入服务端。` });
    } finally {
      setSaving(false);
    }
  }

  function save() {
    if (error) return;
    const body = buildPromptBody(draft, prompts.defaultSystemPrompt);
    put(body.systemPrompt, body.systemPrompt ? '已保存，将在下一轮对话生效（进行中的任务不受影响）。' : '内容与内置默认一致，已改为使用内置默认，将在下一轮对话生效。');
  }

  function restoreDefault() {
    if (!window.confirm('恢复为内置默认提示词？已保存的自定义提示词将被清除（当前草稿也会被替换）。')) return;
    put('', '已恢复内置默认，将在下一轮对话生效。');
  }

  async function onImport(e) {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    if (dirty && !window.confirm('导入会替换编辑器中未保存的草稿。继续？')) return;
    try {
      const { systemPrompt } = await readPromptFile(file);
      setDraft(systemPrompt);
      setMsg({ tone: 'ok', text: `已从 ${file.name} 导入到草稿（${systemPrompt.length} 字符）。确认无误后请点击“保存”。` });
    } catch (err) {
      setMsg({ tone: 'err', text: `导入失败：${err.message}` });
    }
  }

  if (loadErr)
    return (
      <section className="card cs-card">
        <h2>助手系统提示词</h2>
        <div className="alert alert-err">
          <TriangleAlert size={15} />
          <span>读取提示词失败：{loadErr}</span>
        </div>
        <button type="button" className="btn btn-sm" onClick={load}>
          重试
        </button>
      </section>
    );
  if (!prompts)
    return (
      <section className="card cs-card">
        <h2>助手系统提示词</h2>
        <div className="muted small">
          <LoaderCircle size={14} className="spin" /> 读取中…
        </div>
      </section>
    );

  return (
    <section className="card cs-card" aria-labelledby="cs-prompt">
      <div className="row between">
        <h2 id="cs-prompt">助手系统提示词</h2>
        <span className="row gap">
          <span className={cx('tag', overridden ? 'tag-ok' : '')}>{overridden ? '自定义' : '内置默认'}</span>
          {dirty && <span className="tag tag-warn">有未保存的修改</span>}
        </span>
      </div>
      <div className="hint">修改在保存后的下一轮对话生效；进行中的任务继续使用原提示词。清空并保存等同于恢复内置默认。</div>

      <label className="field field-wide">
        <span className="label">提示词内容</span>
        <textarea
          className="mono"
          rows={14}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          spellCheck={false}
          aria-invalid={Boolean(error)}
          placeholder="留空表示使用内置默认提示词"
        />
        <span className={cx('small', error ? 'err-text' : 'muted')}>
          {error || `${draft.length} / ${MAX_PROMPT_CHARS} 字符`}
        </span>
      </label>

      {msg && (
        <div className={cx('alert', msg.tone === 'ok' ? 'alert-ok' : 'alert-err')} role="status">
          {msg.tone === 'ok' ? <CircleCheck size={15} /> : <TriangleAlert size={15} />}
          <span>{msg.text}</span>
        </div>
      )}
      {busy && <div className="small warn-text">后端正在执行任务，保存可能被拒绝（409）；草稿会保留，可稍后重试。</div>}

      <div className="row gap cs-actions">
        <button type="button" className="btn btn-primary btn-sm" onClick={save} disabled={!dirty || saving || Boolean(error)}>
          {saving ? <LoaderCircle size={14} className="spin" /> : <Save size={14} />}
          保存
        </button>
        <button type="button" className="btn btn-sm" onClick={() => fileRef.current?.click()} disabled={saving}>
          <FileUp size={14} />
          导入…
        </button>
        <input ref={fileRef} type="file" accept={IMPORT_ACCEPT} hidden onChange={onImport} aria-label="导入提示词文件" />
        <button type="button" className="btn btn-sm" onClick={() => download(exportPayload(draft, 'txt'))} disabled={!draft} title="导出当前草稿为 UTF-8 文本">
          <Download size={14} />
          导出 TXT
        </button>
        <button type="button" className="btn btn-sm" onClick={() => download(exportPayload(draft, 'json'))} disabled={!draft} title='导出为 {"systemPrompt": "..."}'>
          <Download size={14} />
          导出 JSON
        </button>
        <button type="button" className="btn btn-ghost btn-sm" onClick={() => setDraft(effectivePrompt(prompts))} disabled={saving || !dirty}>
          <Undo2 size={14} />
          放弃修改
        </button>
        <button type="button" className="btn btn-ghost btn-sm" onClick={restoreDefault} disabled={saving || !overridden}>
          <RotateCcw size={14} />
          恢复内置默认
        </button>
      </div>
      <div className="hint">导入支持 UTF-8 编码的 .txt / .md，或 {'{"systemPrompt": "..."}'} 格式的 .json；导入只更新草稿，不会自动保存。</div>
    </section>
  );
}
