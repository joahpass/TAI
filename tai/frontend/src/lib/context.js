import { interpretMcp } from '../mcp.js';

// 从 GetState / GetProject 的 MCP 结果中提取可展示的上下文。字段名未知时返回 null，不臆造。

function pick(obj, keys) {
  if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return undefined;
  for (const k of keys) {
    const v = obj[k];
    if (typeof v === 'string' && v.trim()) return v.trim();
  }
  return undefined;
}

/** @returns {boolean|null} */
export function connectedFromState(result) {
  const { data, isError } = interpretMcp(result);
  if (isError || !data || typeof data !== 'object' || Array.isArray(data)) return null;
  const v = data.IsConnected ?? data.isConnected ?? data.Connected ?? data.connected;
  return typeof v === 'boolean' ? v : null;
}

/** @returns {{name?:string, path?:string}|null} */
export function projectFrom(result) {
  const { data, isError } = interpretMcp(result);
  if (isError || !data || typeof data !== 'object') return null;
  const candidates = [data, data.Project, data.project, data.ProjectInfo, data.projectInfo];
  for (const c of candidates) {
    const name = pick(c, ['ProjectName', 'projectName', 'Name', 'name']);
    const path = pick(c, ['ProjectPath', 'projectPath', 'Path', 'path', 'FullName', 'fullName']);
    if (name || path) return { name, path };
  }
  return null;
}

/** 从路径中取工程文件名作为名称兜底。 */
export function projectLabel(project) {
  if (!project) return '';
  if (project.name) return project.name;
  const p = String(project.path || '');
  const base = p.split(/[\\/]/).filter(Boolean).pop() || '';
  return base.replace(/\.ap\d+$/i, '') || p;
}
