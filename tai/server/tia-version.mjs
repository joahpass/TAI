import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const supportedTiaVersions = Object.freeze([19, 21]);
export function assertTiaVersion(version) {
  if (!supportedTiaVersions.includes(version)) throw new Error('TIA Portal 版本仅支持 19 或 21');
  return version;
}
export function resolveMcpPath(config, exists = fs.existsSync) {
  const version = assertTiaVersion(config.tiaVersion);
  const configured = config.mcpPath;
  // Managed install/development paths follow the selected version; custom binaries stay explicit.
  let candidate;
  if (/[\\/]mcp[\\/](?:v(?:19|21)[\\/])?TiaMcpServer\.exe$/i.test(configured))
    candidate = configured.replace(/([\\/]mcp[\\/])(?:v(?:19|21)[\\/])?TiaMcpServer\.exe$/i, `$1v${version}/TiaMcpServer.exe`);
  else if (/[\\/]mcp-v21[\\/]bin[\\/](?:V(?:19|21)[\\/])?Release[\\/]net48[\\/]TiaMcpServer\.exe$/i.test(configured))
    candidate = configured.replace(/([\\/]mcp-v21[\\/]bin[\\/])(?:V(?:19|21)[\\/])?Release/i, `$1V${version}/Release`);
  else candidate = configured;
  if (!exists(candidate)) throw new Error(`未找到 TIA V${version} MCP 程序：${candidate}。请安装包含此版本的安装包。`);
  return candidate;
}
export function installationDefaults() {
  const location = fileURLToPath(new URL('../../tia-runtime.json', import.meta.url));
  try {
    const data = JSON.parse(fs.readFileSync(location, 'utf8').replace(/^\uFEFF/, ''));
    assertTiaVersion(data.tiaVersion);
    if (typeof data.tiaLocation !== 'string') throw new Error('tiaLocation must be a string');
    return { tiaVersion: data.tiaVersion, tiaLocation: data.tiaLocation };
  } catch (error) { if (error.code === 'ENOENT') return {}; throw new Error(`TIA 安装配置读取失败：${error.message}`); }
}
export function defaultTiaConfig(env = process.env) {
  const installed = installationDefaults();
  const inferred = /Portal V(19|21)(?:[\\/]|$)/i.exec(env.TAI_TIA_LOCATION || '');
  const version = assertTiaVersion(Number(env.TAI_TIA_VERSION || inferred?.[1] || installed.tiaVersion || 19));
  return { tiaVersion: version, tiaLocation: env.TAI_TIA_LOCATION || (installed.tiaVersion === version ? installed.tiaLocation : '') || path.win32.join(env.ProgramFiles || 'C:\\Program Files', 'Siemens', 'Automation', `Portal V${version}`) };
}
