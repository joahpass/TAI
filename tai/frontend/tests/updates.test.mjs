import test from 'node:test';
import assert from 'node:assert/strict';
import { api } from '../src/api.js';
import { releaseLink, updateSummary } from '../src/lib/updates.js';

test('update API uses local GET endpoint and explicit refresh only', async () => {
  const previous = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push({ url, method: init.method });
    return new Response(JSON.stringify({ status: 'unconfigured', currentVersion: '0.9.4' }), { status: 200 });
  };
  try {
    assert.equal((await api.updates()).currentVersion, '0.9.4');
    await api.updates(true);
    assert.deepEqual(calls, [{ url: '/api/updates', method: 'GET' }, { url: '/api/updates?refresh=1', method: 'GET' }]);
    globalThis.fetch = async () => { throw new Error('offline'); };
    await assert.rejects(api.updates(), /本地服务/);
  } finally { globalThis.fetch = previous; }
});

test('update presentation distinguishes unconfigured/errors/current/new', () => {
  assert.match(updateSummary(null), /读取/);
  assert.match(updateSummary({ status: 'unconfigured' }), /未配置/);
  assert.equal(updateSummary({ status: 'error', error: 'GitHub 请求受限' }), 'GitHub 请求受限');
  assert.match(updateSummary({ status: 'ok', updateAvailable: false }), /最新稳定/);
  assert.match(updateSummary({ status: 'ok', updateAvailable: true, latestVersion: '0.10.0' }), /0\.10\.0/);
});

test('release links only appear for new releases on HTTPS github.com', () => {
  const base = { status: 'ok', updateAvailable: true, releaseUrl: 'https://github.com/example/tai/releases/tag/v0.10.0' };
  assert.equal(releaseLink(base), base.releaseUrl);
  assert.equal(releaseLink({ ...base, updateAvailable: false }), null);
  assert.equal(releaseLink({ ...base, status: 'error' }), null);
  for (const releaseUrl of ['javascript:alert(1)', 'http://github.com/a/b/releases/tag/v1', 'https://evil.test/a/b/releases/tag/v1', 'https://user:secret@github.com/a/b/releases/tag/v1', 'https://github.com/a/b/releases/tag/v1?token=secret']) assert.equal(releaseLink({ ...base, releaseUrl }), null);
});
