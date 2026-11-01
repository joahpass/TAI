import test from 'node:test';
import assert from 'node:assert/strict';
import { inspectText, searchText } from '../server/artifact-analysis.mjs';

test('PLC declarations, quotes, languages and lexical disclaimer', () => {
  const text = '// FUNCTION "Fake"\nFUNCTION_BLOCK "Motor ""A"""\n{ S7_Language := \'SCL\'; PreferredLanguage := "en-US" }\n  "Motor DB".Run := TRUE;\n(* DATA_BLOCK "Comment" *)\nDATA_BLOCK DB1\nTYPE "Config"\n';
  const result = inspectText(text, 'Blocks/PROGRAM.SCL');
  assert.equal(result.formatHint, '.scl');
  assert.deepEqual(result.declarations.map(x => [x.type, x.name]), [
    ['FUNCTION_BLOCK', 'Motor "A"'], ['DATA_BLOCK', 'DB1'], ['TYPE', 'Config']
  ]);
  assert.equal(result.declarations[0].line, 2);
  assert.equal(result.declarations[0].offset, text.indexOf('FUNCTION_BLOCK'));
  assert.deepEqual(result.languages.map(x => x.value), ['SCL', 'en-US']);
  assert.ok(result.quotedReferences.some(x => x.name === 'Motor DB'));
  assert.match(result.validity, /neither syntax nor PLC semantic validity/);
});

test('XML root and engineering names from attributes and text', () => {
  const text = '<?xml version="1.0"?>\n<!-- <Fake/> -->\n<Document><SW.Blocks.FB ID="1"><AttributeList><Name>Motor &amp; Pump</Name><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList><SW.Blocks.CompileUnit Name="Logic" /></SW.Blocks.FB><Hmi.Screen Name="Main &quot;View&quot;" /></Document>';
  const result = inspectText(text, 'export.xml');
  assert.equal(result.xml.topTag, 'Document');
  assert.deepEqual(result.xml.objects.map(x => [x.type, x.name]), [
    ['SW.Blocks.FB', 'Motor & Pump'], ['SW.Blocks.CompileUnit', 'Logic'], ['Hmi.Screen', 'Main "View"']
  ]);
  assert.equal(result.languages[0].value, 'SCL');
  assert.equal(result.xml.objects[0].offset, text.indexOf('<SW.Blocks.FB'));
});

test('import guidance distinguishes tool formats without claiming validity', () => {
  assert.equal(inspectText('invalid', 'a.xml').importGuidance.route, 'ImportBlock');
  assert.equal(inspectText('invalid', 'a.s7dcl').importGuidance.route, 'ImportFromDocuments');
  assert.equal(inspectText('invalid', 'a.scl').importGuidance.route, 'ImportExternalSource');
  assert.equal(inspectText('invalid', 'a.scl').importGuidance.requiresRuntimeCapability, true);
  assert.equal(inspectText('invalid', 'a.s7dcl').importGuidance.requiresRuntimeCapability, true);
  for (const name of ['a.txt', 'a.awl', 'no-extension']) {
    assert.equal(inspectText('', name).importGuidance.supportedByTheseTools, false);
    assert.equal(inspectText('', name).importGuidance.route, null);
  }
});

test('literal searches escape punctuation and support multiline and case options', () => {
  const text = 'a.*[x]\r\nNext\na.*[X]\r\nNEXT';
  assert.equal(searchText(text, { query: '.*[x]' }).matches.length, 1);
  assert.equal(searchText(text, { query: '.*[x]', caseSensitive: false }).matches.length, 2);
  const result = searchText(text, { query: '[x]\r\nNext' });
  assert.equal(result.matches[0].line, 1);
  assert.equal(result.matches[0].column, 4);
  assert.equal(result.matches[0].endLine, 2);
  assert.equal(result.matches[0].endColumn, 5);
  assert.equal(result.hasMore, false);
  assert.equal(result.nextOffset, null);
});

test('pagination uses nonoverlapping matches and accurate exhaustion', () => {
  const text = 'aa aa aa';
  const first = searchText(text, { query: 'aa', limit: 2 });
  assert.deepEqual(first.matches.map(x => x.offset), [0, 3]);
  assert.equal(first.hasMore, true);
  assert.equal(first.nextOffset, 5);
  const second = searchText(text, { query: 'aa', offset: first.nextOffset, limit: 2 });
  assert.deepEqual(second.matches.map(x => x.offset), [6]);
  assert.equal(second.hasMore, false);
  assert.equal(second.nextOffset, null);
  assert.equal(searchText('aaaa', { query: 'aa', limit: 2 }).hasMore, false);
  assert.equal(searchText('abc', { query: 'z' }).hasMore, false);
  assert.throws(() => searchText(text, { query: '' }), /non-empty/);
});

test('UTF-16 surrogate positions and offsets remain stable with insensitive matching', () => {
  const text = '😀İX\n🛠️X';
  const result = searchText(text, { query: 'x', caseSensitive: false });
  assert.deepEqual(result.matches.map(x => [x.offset, x.line, x.column]), [[3, 1, 4], [8, 2, 4]]);
  assert.equal(searchText(text, { query: '😀' }).matches[0].endOffset, 2);
});

test('search output is bounded even on huge lines and many matches', () => {
  const result = searchText('a'.repeat(20000), { query: 'a', limit: 999, contextLines: 999 });
  assert.equal(result.matches.length, 100);
  assert.equal(result.contextLines, 5);
  assert.equal(result.hasMore, true);
  assert.ok(result.matches.every(x => x.context.length <= 11 && x.context.every(y => y.text.length <= 1000)));
  assert.equal(result.matches[0].context[0].truncated, true);
});

test('inspection bounds metadata, values and scan size explicitly', () => {
  const many = inspectText(Array.from({ length: 110 }, (_, i) => `FUNCTION "F${i}"`).join('\n'), 'x.scl');
  assert.equal(many.declarations.length, 100);
  assert.equal(many.quotedReferences.length, 100);
  assert.equal(many.truncated, true);
  const long = inspectText(`FUNCTION "${'n'.repeat(1000)}"`, 'x.scl');
  assert.equal(long.declarations[0].name.length, 512);
  assert.equal(long.truncated, true);
  const large = inspectText(' '.repeat(2 * 1024 * 1024 + 10), 'x.scl');
  assert.equal(large.scannedLength, 2 * 1024 * 1024);
  assert.ok(large.truncatedCategories.includes('scan'));
});
