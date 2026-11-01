// 两个独立界面：/assistant（TIA 所属窗口中的紧凑工程助手）与 /（配置与诊断控制台）。
// 后端对未知路径回退 index.html，因此由前端按 pathname 选择界面。

export const ASSISTANT_PATH = '/assistant';
export const CONSOLE_PATH = '/';

function normalize(pathname) {
  let p = String(pathname || '/');
  try {
    p = decodeURIComponent(p);
  } catch {
    /* 保留原值 */
  }
  p = p.replace(/\/{2,}/g, '/').replace(/\/+$/, '');
  return (p || '/').toLowerCase();
}

/** @returns {'assistant'|'console'} */
export function surfaceFor(pathname) {
  const p = normalize(pathname);
  if (p === '/workspace' || p.startsWith('/workspace/')) return 'workspace';
  return p === ASSISTANT_PATH || p.startsWith(`${ASSISTANT_PATH}/`) ? 'assistant' : 'console';
}

/** 控制台绝对地址（同源）。在 TAI 桌面窗口中，由宿主决定是否交给系统浏览器打开。 */
export function consoleUrl(origin) {
  return new URL(CONSOLE_PATH, origin).href;
}

export function assistantUrl(origin) {
  return new URL(ASSISTANT_PATH, origin).href;
}
