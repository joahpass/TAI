import path from 'node:path';
import { resultData, checkedResult } from './results.mjs';
export function normalizeProjectPath(value) {
  if(typeof value!=='string'||!value.trim()) return null;
  return path.win32.normalize(value.trim().replaceAll('/','\\')).replace(/\\+$/,'').toLowerCase();
}
export async function verifyProjectContext(mcp,project) {
  if(!normalizeProjectPath(project?.tiaProjectPath)) throw new Error('项目上下文错误：当前工作台项目未绑定 TIA 工程');
  let response;
  try { response=checkedResult(await mcp.call('GetState',{})); }
  catch(error) { throw new Error(`项目上下文错误：读取 TIA 状态失败：${error.message}`); }
  const state=resultData(response);
  if(response.isError || (state?.IsConnected??state?.isConnected)!==true) throw new Error('项目上下文错误：TIA 工程未连接');
  const actual=normalizeProjectPath(state?.ProjectPath??state?.projectPath);
  if(!actual) throw new Error('项目上下文错误：当前 TIA 工程路径未知');
  if(actual!==normalizeProjectPath(project.tiaProjectPath)) throw new Error('项目上下文错误：当前 TIA 工程与项目绑定不匹配');
  return state;
}
