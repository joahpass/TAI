// This module reports lexical evidence only. It does not parse or validate PLC code.
const SCAN_LIMIT = 2 * 1024 * 1024;
const ITEM_LIMIT = 100;
const VALUE_LIMIT = 512;
const CONTEXT_WIDTH = 1000;
const clip = value => value.slice(0, VALUE_LIMIT);

function requireText(text) {
  if (typeof text !== 'string') throw new TypeError('text must be a string');
}
function integer(value, fallback, max) {
  return Number.isFinite(value) ? Math.min(max, Math.max(0, Math.floor(value))) : fallback;
}
function locator(text) {
  let cursor = 0, line = 1, lineStart = 0;
  return offset => {
    for (; cursor < offset; cursor++) {
      if (text[cursor] === '\n') { line++; lineStart = cursor + 1; }
    }
    return { line, column: offset - lineStart + 1, offset };
  };
}
function contextAt(text, start, line, radius) {
  let from = text.lastIndexOf('\n', start - 1) + 1;
  if (start === 0) from = 0;
  let firstLine = line;
  for (let i = 0; i < radius && from > 0; i++) {
    from = text.lastIndexOf('\n', from - 2) + 1;
    firstLine--;
  }
  // Context is centered on the starting line, even for a very long multiline match.
  const result = [];
  for (let n = 0; n < radius * 2 + 1 && from <= text.length; n++) {
    let to = text.indexOf('\n', from);
    if (to < 0) to = text.length;
    const contentEnd = to > from && text[to - 1] === '\r' ? to - 1 : to;
    const windowStart = firstLine + n === line
      ? Math.max(from, Math.min(start - 200, contentEnd - CONTEXT_WIDTH)) : from;
    const windowEnd = Math.min(contentEnd, windowStart + CONTEXT_WIDTH);
    result.push({ line: firstLine + n, text: text.slice(windowStart, windowEnd),
      offset: windowStart, truncated: windowStart > from || windowEnd < contentEnd });
    if (to === text.length) break;
    from = to + 1;
  }
  return result;
}

/** Literal, non-overlapping matches. Offsets/columns are UTF-16; lines are LF-based (CRLF supported). */
export function searchText(text, { query, offset = 0, limit = 50, contextLines = 2, caseSensitive = true } = {}) {
  requireText(text);
  if (typeof query !== 'string' || query.length === 0) throw new TypeError('query must be a non-empty string');
  offset = integer(offset, 0, text.length);
  limit = Math.max(1, integer(limit, 50, 100));
  contextLines = integer(contextLines, 2, 5);
  caseSensitive = caseSensitive !== false;
  // Only escaped literals reach RegExp; callers cannot supply regex syntax.
  const pattern = caseSensitive ? null : new RegExp(query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'gi');
  const find = from => {
    if (!pattern) return text.indexOf(query, from);
    pattern.lastIndex = from;
    return pattern.exec(text)?.index ?? -1;
  };
  const locate = locator(text);
  const matches = [];
  let cursor = offset;
  while (matches.length < limit) {
    const found = find(cursor);
    if (found < 0) break;
    const begin = locate(found);
    const endOffset = found + query.length;
    const end = locate(endOffset);
    matches.push({ ...begin, endOffset, endLine: end.line, endColumn: end.column,
      context: contextAt(text, found, begin.line, contextLines) });
    cursor = endOffset;
  }
  const hasMore = find(cursor) >= 0;
  return { query, offset, limit, contextLines, caseSensitive, matches,
    nextOffset: hasMore ? cursor : null, hasMore };
}

function withoutComments(text) {
  // Preserve positions and line endings while suppressing common PLC comments.
  const parts = [];
  let cursor = 0, segment = 0, quote = null;
  while (cursor < text.length) {
    const c = text[cursor], next = text[cursor + 1];
    if (quote) {
      if (c === '$' || c === '\\') { cursor += 2; continue; }
      if (c === quote) {
        if (next === quote) { cursor += 2; continue; }
        quote = null;
      }
      cursor++; continue;
    }
    if (c === '"' || c === "'") { quote = c; cursor++; continue; }
    let end = -1;
    if (c === '/' && next === '/') {
      end = text.indexOf('\n', cursor + 2);
    } else if ((c === '/' && next === '*') || (c === '(' && next === '*')) {
      const close = c === '/' ? '*/' : '*)';
      const found = text.indexOf(close, cursor + 2);
      end = found < 0 ? text.length : found + 2;
    } else { cursor++; continue; }
    if (end < 0) end = text.length;
    parts.push(text.slice(segment, cursor), text.slice(cursor, end).replace(/[^\r\n]/g, ' '));
    cursor = end; segment = end;
  }
  parts.push(text.slice(segment));
  return parts.join('');
}
function decodeXml(value) {
  return value.replace(/&(amp|lt|gt|quot|apos);/g, (_, name) => ({ amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" })[name]);
}

export function inspectText(text, relativeName = '') {
  requireText(text);
  const extension = String(relativeName).toLowerCase().match(/\.[a-z0-9]+$/)?.[0] ?? '';
  const formatHint = extension || 'unknown';
  const source = text.slice(0, SCAN_LIMIT);
  const result = {
    formatHint, analysisKind: 'bounded lexical outline',
    validity: 'Not checked: this outline establishes neither syntax nor PLC semantic validity.',
    offsetEncoding: 'UTF-16', lineConvention: '1-based LF lines and UTF-16 columns; CRLF supported',
    textLength: text.length, scannedLength: source.length,
    bounds: { scanLength: SCAN_LIMIT, itemsPerCategory: ITEM_LIMIT, valueLength: VALUE_LIMIT },
    declarations: [], languages: [], quotedReferences: [], xml: { topTag: null, objects: [] },
    truncated: text.length > source.length, truncatedCategories: [],
    importGuidance: extension === '.xml'
      ? { route: 'ImportBlock', supportedByTheseTools: true, note: 'ImportBlock accepts XML only; extension and lexical evidence do not establish importability.' }
      : extension === '.s7dcl'
        ? { route: 'ImportFromDocuments', supportedByTheseTools: true, requiresRuntimeCapability: true, note: 'SIMATIC SD requires a supported V20+ backend; V19 reports this API as unavailable. Importability is not checked.' }
        : extension === '.scl'
          ? { route: 'ImportExternalSource', supportedByTheseTools: true, requiresRuntimeCapability: true, note: 'Standard SCL uses ImportExternalSource only when the running backend advertises it. Never rename SCL to XML or SIMATIC SD; generated blocks/types require compilation.' }
          : { route: null, supportedByTheseTools: false, note: 'No import route is registered for this extension. XML, SIMATIC SD and standard SCL are distinct formats.' }
  };
  const truncated = category => {
    result.truncated = true;
    if (!result.truncatedCategories.includes(category)) result.truncatedCategories.push(category);
  };
  if (text.length > source.length) truncated('scan');
  const add = (category, array, item) => {
    if (array.length >= ITEM_LIMIT) { truncated(category); return; }
    for (const [key, value] of Object.entries(item)) {
      if (typeof value === 'string' && value.length > VALUE_LIMIT) { item[key] = clip(value); truncated(category); }
    }
    array.push(item);
  };
  let position = locator(source);
  if (extension !== '.xml') {
    const clean = withoutComments(source);
    const declaration = /^[\t ]*(FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK|TYPE)\b[\t ]+("(?:[^"\r\n]|"")*"|[A-Za-z_][\w]*|[0-9]+)/gim;
    for (const match of clean.matchAll(declaration)) {
      const offset = match.index + match[0].search(/\S/);
      add('declarations', result.declarations, { type: match[1].toUpperCase(), name: match[2].startsWith('"') ? match[2].slice(1, -1).replace(/""/g, '"') : match[2], ...position(offset), endOffset: match.index + match[0].length });
      if (result.truncatedCategories.includes('declarations')) break;
    }
    position = locator(source);
    const languages = /\b(S7_Language|PreferredLanguage)\b\s*[:=]+\s*(?:'([^'\r\n]*)'|"([^"\r\n]*)"|([A-Za-z_][\w-]*))/gi;
    for (const match of clean.matchAll(languages)) {
      add('languages', result.languages, { property: match[1], value: match[2] ?? match[3] ?? match[4], ...position(match.index) });
      if (result.truncatedCategories.includes('languages')) break;
    }
    position = locator(source);
    const quotes = /"(?:[^"\r\n]|"")*"/g;
    for (const match of clean.matchAll(quotes)) {
      add('quotedReferences', result.quotedReferences, { name: match[0].slice(1, -1).replace(/""/g, '"'), kind: 'quoted symbol candidate (may be a declaration or string)', ...position(match.index), endOffset: match.index + match[0].length });
      if (result.quotedReferences.length >= ITEM_LIMIT) { if (quotes.test(clean.slice(match.index + match[0].length))) truncated('quotedReferences'); break; }
    }
  } else {
    // A bounded lexical tag walk, not an XML parser; no entities or external resources are resolved.
    const stack = [];
    let pendingName = null;
    let tagCursor = 0;
    while (tagCursor < source.length) {
      const index = source.indexOf('<', tagCursor);
      if (index < 0) break;
      if (source.startsWith('<!--', index) || source.startsWith('<![CDATA[', index)) {
        const terminator = source.startsWith('<!--', index) ? '-->' : ']]>';
        const end = source.indexOf(terminator, index + 4);
        tagCursor = end < 0 ? source.length : end + terminator.length;
        continue;
      }
      const end = source.indexOf('>', index + 1);
      if (end < 0) break;
      tagCursor = end + 1;
      if (end - index > 4096) { truncated('xml.tags'); continue; }
      const raw = source.slice(index, tagCursor);
      const match = { index };
      if (raw.startsWith('<?') || raw.startsWith('<!')) continue;
      const tag = /^<\s*(\/?)\s*([\w.:-]+)/.exec(raw);
      if (!tag) continue;
      const closing = Boolean(tag[1]), name = tag[2];
      if (closing) {
        if (name === 'Name' && pendingName) {
          const value = source.slice(pendingName.start, match.index);
          if (!value.includes('<') && !pendingName.object.name) pendingName.object.name = clip(decodeXml(value.trim()));
          if (value.length > VALUE_LIMIT) truncated('xml.objects');
          pendingName = null;
        }
        if (stack.at(-1)?.type === name) stack.pop();
        continue;
      }
      if (!result.xml.topTag) result.xml.topTag = clip(name);
      if (/^(?:SW\.|Hmi\.|HW\.|Engineering\.)/.test(name)) {
        const attr = /\bName\s*=\s*(?:"([^"]*)"|'([^']*)')/.exec(raw);
        const object = { type: name, name: attr ? decodeXml(attr[1] ?? attr[2]) : null, ...position(match.index) };
        add('xml.objects', result.xml.objects, object);
        if (!raw.endsWith('/>') && stack.length < 128) stack.push(object);
        else if (stack.length >= 128) truncated('xml.depth');
      }
      if (name === 'Name' && stack.length) pendingName = { object: stack.at(-1), start: match.index + raw.length };
      if (name === 'S7_Language' || name === 'PreferredLanguage' || name === 'ProgrammingLanguage') {
        const end = source.indexOf('<', match.index + raw.length);
        if (end >= 0) add('languages', result.languages, { property: name, value: decodeXml(source.slice(match.index + raw.length, end).trim()), ...position(match.index) });
      }
    }
  }
  return result;
}
