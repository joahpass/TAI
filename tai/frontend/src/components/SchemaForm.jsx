import { useState } from 'react';

// 基于 JSON Schema 的参数表单。简单类型生成控件，复杂类型使用 JSON 文本框。

function initialValue(schema) {
  if (schema?.default !== undefined) return schema.default;
  if (schema?.type === 'boolean') return false;
  return undefined;
}

export function initialArgs(schema) {
  const out = {};
  for (const [k, s] of Object.entries(schema?.properties ?? {})) {
    const v = initialValue(s);
    if (v !== undefined) out[k] = v;
  }
  return out;
}

function typeOf(s) {
  const t = Array.isArray(s?.type) ? s.type.find((x) => x !== 'null') : s?.type;
  if (s?.enum) return 'enum';
  return t || 'json';
}

function Field({ name, schema, required, value, onChange }) {
  const t = typeOf(schema);
  const [jsonText, setJsonText] = useState(() => (value === undefined ? '' : JSON.stringify(value, null, 2)));
  const [jsonErr, setJsonErr] = useState('');
  const id = `arg-${name}`;
  let control;
  if (t === 'enum') {
    control = (
      <select id={id} value={value ?? ''} onChange={(e) => onChange(e.target.value === '' ? undefined : schema.enum.find((x) => String(x) === e.target.value))}>
        <option value="">（未选择）</option>
        {schema.enum.map((x) => (
          <option key={String(x)} value={String(x)}>
            {String(x)}
          </option>
        ))}
      </select>
    );
  } else if (t === 'boolean') {
    control = (
      <label className="check">
        <input id={id} type="checkbox" checked={Boolean(value)} onChange={(e) => onChange(e.target.checked)} />
        <span>{value ? 'true' : 'false'}</span>
      </label>
    );
  } else if (t === 'integer' || t === 'number') {
    control = (
      <input
        id={id}
        type="number"
        step={t === 'integer' ? 1 : 'any'}
        value={value ?? ''}
        onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))}
      />
    );
  } else if (t === 'string') {
    control = <input id={id} type="text" value={value ?? ''} onChange={(e) => onChange(e.target.value === '' ? undefined : e.target.value)} />;
  } else {
    control = (
      <>
        <textarea
          id={id}
          rows={3}
          className="mono"
          value={jsonText}
          placeholder={t === 'array' ? '[ ]' : '{ }'}
          onChange={(e) => {
            const text = e.target.value;
            setJsonText(text);
            if (!text.trim()) {
              setJsonErr('');
              onChange(undefined);
              return;
            }
            try {
              onChange(JSON.parse(text));
              setJsonErr('');
            } catch {
              setJsonErr('JSON 格式无效');
            }
          }}
        />
        {jsonErr && <div className="field-err">{jsonErr}</div>}
      </>
    );
  }
  return (
    <div className="field">
      <label htmlFor={id}>
        <span className="mono">{name}</span>
        {required && <span className="req">*</span>}
        <span className="muted small"> {t}</span>
      </label>
      {control}
      {schema?.description && <div className="hint">{schema.description}</div>}
    </div>
  );
}

export default function SchemaForm({ schema, value, onChange }) {
  const props = Object.entries(schema?.properties ?? {});
  const required = new Set(schema?.required ?? []);
  if (!props.length) return <div className="muted small">此工具无需参数。</div>;
  props.sort((a, b) => Number(required.has(b[0])) - Number(required.has(a[0])));
  return (
    <div className="schema-form">
      {props.map(([k, s]) => (
        <Field
          key={k}
          name={k}
          schema={s}
          required={required.has(k)}
          value={value[k]}
          onChange={(v) => {
            const next = { ...value };
            if (v === undefined) delete next[k];
            else next[k] = v;
            onChange(next);
          }}
        />
      ))}
    </div>
  );
}

export function missingRequired(schema, value) {
  return (schema?.required ?? []).filter((k) => value[k] === undefined || value[k] === '');
}
