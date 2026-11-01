import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { createUpdateChecker, compareVersions, githubRepository, stableVersion } from './updates.mjs';
import { createTaiServer } from './index.mjs';
const metadata = { version: '0.9.4', repository: { url: 'https://github.com/example/tai.git' } };
const release = (tag_name, extra = {}) => ({ ok: true, json: async () => ({ tag_name, draft: false, prerelease: false, ...extra }) });

test('stable SemVer numeric ordering and invalid/pre-release versions', () => {
  assert.equal(compareVersions('0.10.0', '0.9.9'), 1);
  assert.equal(compareVersions('v1.0.0', '1.0.0+build.3'), 0);
  assert.equal(compareVersions('1.2.9', '1.3.0'), -1);
  assert.equal(compareVersions('10000000000000000001.0.0', '9999999999999999999.0.0'), 1);
  for (const value of ['1.2.3-beta.1', '1.2', '01.2.3', '1.2.3<script>', null]) assert.equal(stableVersion(value), null);
});

test('repository configuration accepts only GitHub repository identities', () => {
  for (const value of ['example/tai', 'https://github.com/example/tai.git', 'git+https://github.com/example/tai.git', 'git@github.com:example/tai.git']) assert.equal(githubRepository(value), 'example/tai');
  for (const value of ['https://evil.test/example/tai', 'http://github.com/example/tai', 'https://github.com:443/example/tai', 'https://user:secret@github.com/example/tai', 'a/..', 'a/b?token=secret', 'a/b/releases/latest']) assert.equal(githubRepository(value), null);
});

test('new release, equal/older versions, deterministic HTTPS API and safe link', async () => {
  for (const [tag, expected] of [['v0.10.0', true], ['0.9.4', false], ['0.9.3', false]]) {
    const checker = createUpdateChecker({ metadata, repository: 'example/tai', fetchImpl: async (url, init) => {
      assert.equal(url, 'https://api.github.com/repos/example/tai/releases/latest');
      assert.equal(init.redirect, 'error');
      assert.equal('Authorization' in init.headers, false);
      return release(tag, { html_url: 'https://evil.test/' });
    } });
    const result = await checker();
    assert.equal(result.status, 'ok');
    assert.equal(result.updateAvailable, expected);
    assert.equal(result.releaseUrl, `https://github.com/example/tai/releases/tag/${tag}`);
  }
});

test('unconfigured and invalid configuration never fetch; explicit override wins', async () => {
  let calls = 0;
  const fetchImpl = async () => { calls++; return release('1.0.0'); };
  assert.equal((await createUpdateChecker({ metadata: { version: '1.0.0' }, repository: '', fetchImpl })()).status, 'unconfigured');
  assert.equal((await createUpdateChecker({ metadata, repository: 'https://evil.test/a/b', fetchImpl })()).status, 'error');
  assert.equal(calls, 0);
  assert.equal((await createUpdateChecker({ metadata, repository: 'other/project', fetchImpl })()).repository, 'other/project');
});

test('package repository.url fallback', async () => {
  const previous = process.env.TAI_GITHUB_REPOSITORY;
  delete process.env.TAI_GITHUB_REPOSITORY;
  try { assert.equal((await createUpdateChecker({ metadata, fetchImpl: async () => release('1.0.0') })()).repository, 'example/tai'); }
  finally { if (previous === undefined) delete process.env.TAI_GITHUB_REPOSITORY; else process.env.TAI_GITHUB_REPOSITORY = previous; }
});

test('API errors and malformed/prerelease data are sanitized', async () => {
  const cases = [async () => ({ ok: false, status: 403 }), async () => ({ ok: false, status: 404 }), async () => { throw new Error('token=secret'); }, async () => release('1.0.0-beta'), async () => release('1.0.0', { prerelease: true }), async () => release('1.0.0', { draft: true })];
  for (const fetchImpl of cases) {
    const result = await createUpdateChecker({ metadata, repository: 'example/tai', fetchImpl })();
    assert.equal(result.status, 'error');
    assert.equal(result.updateAvailable, false);
    assert.equal(result.releaseUrl, null);
    assert.doesNotMatch(JSON.stringify(result), /token=secret/);
  }
});

test('timeout, successful/error cache expiry, refresh and concurrent deduplication', async () => {
  const timeout = createUpdateChecker({ metadata, repository: 'example/tai', timeoutMs: 5, fetchImpl: (_, { signal }) => new Promise((resolve, reject) => signal.addEventListener('abort', () => reject(new Error('abort')))) });
  assert.match((await timeout()).error, /超时/);
  let time = 0, calls = 0;
  const checker = createUpdateChecker({ metadata, repository: 'example/tai', now: () => time, cacheMs: 100, errorCacheMs: 10, fetchImpl: async () => { calls++; return calls > 2 ? { ok: false, status: 500 } : release('1.0.0'); } });
  const [a, b] = await Promise.all([checker(), checker({ refresh: true })]);
  assert.equal(a, b); assert.equal(calls, 1);
  await checker(); assert.equal(calls, 1);
  await checker({ refresh: true }); assert.equal(calls, 2);
  time = 101; assert.equal((await checker()).status, 'error'); assert.equal(calls, 3);
  time = 110; await checker(); assert.equal(calls, 3);
  time = 112; await checker(); assert.equal(calls, 4);
});

test('GET updates API forwards refresh and stays independent from MCP', async () => {
  const directory = await mkdtemp(fileURLToPath(new URL('./.updates-test-', import.meta.url)));
  let starts = 0;
  const calls = [];
  const app = await createTaiServer({ directory, mcpClient: { tools: [], start() { starts++; }, stop() {} }, updateChecker: async options => { calls.push(options); return { currentVersion: '0.9.4', status: 'unconfigured' }; } });
  try {
    await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${app.server.address().port}`;
    for (const query of ['', '?refresh=1', '?refresh=https://evil.test']) {
      const response = await fetch(`${base}/api/updates${query}`);
      assert.equal(response.status, 200);
      assert.equal((await response.json()).status, 'unconfigured');
    }
    assert.deepEqual(calls, [{ refresh: false }, { refresh: true }, { refresh: false }]);
    assert.equal(starts, 0);
  } finally { await app.close(); await rm(directory, { recursive: true, force: true }); }
});
