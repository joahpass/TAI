export function updateSummary(data) {
  if (!data) return '正在读取版本信息…';
  if (data.status === 'unconfigured') return '未配置 GitHub 仓库，请由发布者设置更新来源。';
  if (data.status === 'error') return data.error || '检查更新失败，请稍后重试。';
  return data.updateAvailable ? `发现新版本 ${data.latestVersion}` : '当前已是最新稳定版本。';
}

export function releaseLink(data) {
  if (data?.status !== 'ok' || !data.updateAvailable || typeof data.releaseUrl !== 'string') return null;
  try {
    const url = new URL(data.releaseUrl);
    return url.protocol === 'https:' && url.hostname === 'github.com' && !url.port && !url.username && !url.password
      && /^\/[^/]+\/[^/]+\/releases\/tag\/[^/]+$/.test(url.pathname) && !url.search && !url.hash ? url.href : null;
  } catch { return null; }
}
