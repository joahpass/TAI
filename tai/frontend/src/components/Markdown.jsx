import { Fragment } from 'react';
import { CopyButton } from './ResultView.jsx';

// 轻量 Markdown：代码块（带复制）、标题、列表、行内代码、粗体。全部以 React 节点渲染，不注入 HTML。

function inline(text, keyBase) {
  const parts = [];
  const re = /(`[^`\n]+`|\*\*[^*\n]+\*\*)/g;
  let last = 0;
  let m;
  let i = 0;
  while ((m = re.exec(text))) {
    if (m.index > last) parts.push(text.slice(last, m.index));
    const tok = m[0];
    if (tok.startsWith('`')) parts.push(<code key={`${keyBase}-${i++}`}>{tok.slice(1, -1)}</code>);
    else parts.push(<strong key={`${keyBase}-${i++}`}>{tok.slice(2, -2)}</strong>);
    last = m.index + tok.length;
  }
  if (last < text.length) parts.push(text.slice(last));
  return parts;
}

function TextBlock({ text, k }) {
  const lines = text.split('\n');
  const out = [];
  let list = null;
  let para = [];
  const flushPara = () => {
    if (para.length) {
      out.push(
        <p key={`p${out.length}`}>
          {para.map((l, i) => (
            <Fragment key={i}>
              {i > 0 && <br />}
              {inline(l, `${k}-${out.length}-${i}`)}
            </Fragment>
          ))}
        </p>,
      );
      para = [];
    }
  };
  const flushList = () => {
    if (list) {
      const Tag = list.ordered ? 'ol' : 'ul';
      out.push(
        <Tag key={`l${out.length}`}>
          {list.items.map((it, i) => (
            <li key={i}>{inline(it, `${k}-li-${out.length}-${i}`)}</li>
          ))}
        </Tag>,
      );
      list = null;
    }
  };
  for (const line of lines) {
    const h = line.match(/^(#{1,4})\s+(.*)$/);
    const ul = line.match(/^\s*[-*]\s+(.*)$/);
    const ol = line.match(/^\s*\d+[.)]\s+(.*)$/);
    if (h) {
      flushPara();
      flushList();
      out.push(
        <div key={`h${out.length}`} className={`md-h md-h${h[1].length}`}>
          {inline(h[2], `${k}-h${out.length}`)}
        </div>,
      );
    } else if (ul || ol) {
      flushPara();
      const ordered = Boolean(ol);
      if (!list || list.ordered !== ordered) {
        flushList();
        list = { ordered, items: [] };
      }
      list.items.push((ul || ol)[1]);
    } else if (!line.trim()) {
      flushPara();
      flushList();
    } else {
      flushList();
      para.push(line);
    }
  }
  flushPara();
  flushList();
  return out;
}

export function CodeBlock({ lang, code }) {
  return (
    <div className="code">
      <div className="code-bar">
        <span>{lang || '代码'}</span>
        <CopyButton text={code} label="复制代码" />
      </div>
      <pre>
        <code>{code}</code>
      </pre>
    </div>
  );
}

export default function Markdown({ text }) {
  const src = String(text ?? '');
  const blocks = [];
  const re = /```([\w+#.-]*)[^\n]*\n([\s\S]*?)(?:```|$)/g;
  let last = 0;
  let m;
  while ((m = re.exec(src))) {
    if (m.index > last) blocks.push({ type: 'text', text: src.slice(last, m.index) });
    blocks.push({ type: 'code', lang: m[1], code: m[2].replace(/\n$/, '') });
    last = m.index + m[0].length;
  }
  if (last < src.length) blocks.push({ type: 'text', text: src.slice(last) });
  return (
    <div className="md">
      {blocks.map((b, i) =>
        b.type === 'code' ? <CodeBlock key={i} lang={b.lang} code={b.code} /> : <TextBlock key={i} k={i} text={b.text} />,
      )}
    </div>
  );
}
