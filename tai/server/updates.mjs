// Only stable SemVer versions participate; build metadata does not affect precedence.
export function stableVersion(value) {
  const match = typeof value === 'string' && value.match(/^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/);
  return match ? match.slice(1, 4).join('.') : null;
}

export function compareVersions(a, b) {
  const left = stableVersion(a), right = stableVersion(b);
  if (!left || !right) return null;
  const x = left.split('.').map(BigInt), y = right.split('.').map(BigInt);
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] > y[i] ? 1 : -1;
  return 0;
}

export function githubRepository(value) {
  if (typeof value !== 'string') return null;
  const input = value.trim();
  const match = input.match(/^(?:git\+)?https:\/\/github\.com\/([^/?#]+)\/([^/?#]+?)\/?$/)
    || input.match(/^git@github\.com:([^/]+)\/([^/]+)$/)
    || input.match(/^([^/]+)\/([^/]+)$/);
  if (!match) return null;
  const owner = match[1], repo = match[2].replace(/\.git$/, '');
  if (!/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$/.test(owner)
    || !/^[A-Za-z0-9_.-]{1,100}$/.test(repo) || ['.', '..'].includes(repo)) return null;
  return `${owner}/${repo}`;
}

export function createUpdateChecker({ metadata, repository = process.env.TAI_GITHUB_REPOSITORY,
  fetchImpl = globalThis.fetch, now = Date.now, timeoutMs = 8000, cacheMs = 15 * 60_000, errorCacheMs = 60_000 } = {}) {
  const configured = repository !== undefined ? repository : metadata?.repository?.url ?? metadata?.repository;
  const repo = githubRepository(configured);
  const currentVersion = String(metadata?.version ?? '');
  const base = { currentVersion, repository: repo, latestVersion: null, updateAvailable: false, releaseUrl: null, checkedAt: null, error: null };
  let cached = null, expires = 0, pending = null;
  return async function checkUpdates({ refresh = false } = {}) {
    if (!repo) return { ...base, status: configured ? 'error' : 'unconfigured', error: configured ? 'GitHub 仓库配置格式无效，请使用 owner/repo 或 GitHub HTTPS 仓库地址。' : null };
    if (!stableVersion(currentVersion)) return { ...base, status: 'error', error: '当前应用版本不是稳定的 SemVer 版本。' };
    if (pending) return pending;
    if (!refresh && cached && now() < expires) return cached;
    pending = (async () => {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeoutMs);
      let result;
      try {
        const response = await fetchImpl(`https://api.github.com/repos/${repo}/releases/latest`, {
          method: 'GET', redirect: 'error', signal: controller.signal,
          headers: { Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'TAI-update-checker' },
        });
        if (!response.ok) {
          const error = response.status === 404 ? '尚无公开的稳定 GitHub Release，或仓库不可访问。'
            : response.status === 403 || response.status === 429 ? 'GitHub 请求受限，请稍后重试。' : 'GitHub Release 检查失败，请稍后重试。';
          result = { ...base, status: 'error', error };
        } else {
          const release = await response.json();
          const latestVersion = stableVersion(release?.tag_name);
          if (!latestVersion || release.draft || release.prerelease) throw new Error('invalid release');
          // Construct the browser URL from trusted configuration and the validated stable tag.
          const releaseUrl = `https://github.com/${repo}/releases/tag/${encodeURIComponent(release.tag_name)}`;
          result = { ...base, status: 'ok', latestVersion, updateAvailable: compareVersions(latestVersion, currentVersion) > 0, releaseUrl };
        }
      } catch {
        result = { ...base, status: 'error', error: controller.signal.aborted ? '检查更新超时，请稍后重试。' : '检查更新失败，请检查网络或 GitHub Release 数据。' };
      } finally {
        clearTimeout(timer);
      }
      cached = { ...result, checkedAt: new Date(now()).toISOString() };
      expires = now() + (result.status === 'ok' ? cacheMs : errorCacheMs);
      return cached;
    })();
    try { return await pending; } finally { pending = null; }
  };
}
