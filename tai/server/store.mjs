import fs from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { assertTiaVersion, defaultTiaConfig } from './tia-version.mjs';
const developmentMcpPath = fileURLToPath(new URL('../../mcp-v21/bin/V19/Release/net48/TiaMcpServer.exe', import.meta.url));
export const defaults = { provider: 'openai', baseUrl: 'https://api.openai.com/v1', model: '', apiKey: '', temperature: 0.2, maxTokens: 4096, maxSteps: 0, systemPrompt: '', mcpPath: process.env.TAI_MCP_PATH || developmentMcpPath, ...defaultTiaConfig() };
export function validateConfig(input, current) {
  const out = { ...current };
  for (const key of ['provider','baseUrl','model','mcpPath','tiaLocation']) if (input[key] !== undefined) { if (typeof input[key] !== 'string') throw new Error(`${key} 必须为字符串`); out[key] = input[key].trim(); }
  if (!['openai', 'anthropic'].includes(out.provider)) throw new Error('不支持的模型协议');
  const url = new URL(out.baseUrl);
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash) throw new Error('模型地址必须为有效的 HTTP(S) 基础地址');
  out.baseUrl = out.baseUrl.replace(/\/+$/, '');
  for (const [key, min, max] of [['temperature',0,2],['maxTokens',128,65536]]) {
    if (input[key] !== undefined) { const n = input[key]; if (typeof n !== 'number' || !Number.isFinite(n) || n < min || n > max || (key !== 'temperature' && !Number.isInteger(n))) throw new Error(`${key} 取值范围 ${min}–${max}`); out[key] = n; }
  }
  if (input.tiaVersion !== undefined) out.tiaVersion = assertTiaVersion(input.tiaVersion);
  assertTiaVersion(out.tiaVersion);
  const namedVersion = /Portal V(\d+)(?:[\\/]|$)/i.exec(out.tiaLocation);
  if (namedVersion && Number(namedVersion[1]) !== out.tiaVersion) throw new Error('TIA 安装目录版本与所选版本不一致');
  out.maxSteps = 0; // Migrate older fixed budgets; user cancellation remains available.
  if (input.systemPrompt !== undefined) {
    if (typeof input.systemPrompt !== 'string' || input.systemPrompt.length > 100000 || input.systemPrompt.includes('\0')) throw new Error('提示词必须是最多 100000 字符的文本');
    out.systemPrompt = input.systemPrompt;
  }
  if (input.clearApiKey === true) out.apiKey = '';
  else if (input.apiKey) { if (typeof input.apiKey !== 'string') throw new Error('API Key 必须为字符串'); out.apiKey = input.apiKey.trim(); }
  return out;
}
export function publicConfig(config) { const { apiKey, ...rest } = config; return { ...rest, hasApiKey: Boolean(apiKey) }; }
export class Store {
  constructor(directory) { this.directory = directory; this.config = { ...defaults }; this.conversations = []; this.projects = []; this.writes = new Map(); }
  async init() {
    await fs.mkdir(this.directory, { recursive: true, mode: 0o700 });
    for (const key of ['config','conversations','projects']) {
      try { this[key] = JSON.parse(await fs.readFile(path.join(this.directory, `${key}.json`), 'utf8')); }
      catch (error) { if (error.code !== 'ENOENT') throw new Error(`无法读取 ${key}.json：${error.message}`); }
    }
    if (process.env.TAI_MCP_PATH && /[\\/]mcp-v21[\\/]bin[\\/](?:V(?:19|21)[\\/])?Release[\\/]net48[\\/]TiaMcpServer\.exe$/i.test(this.config.mcpPath || '')) {
      this.config.mcpPath = process.env.TAI_MCP_PATH;
    }
    this.config = validateConfig(this.config, defaults);
    const legacy = this.conversations.some(c => !c.projectId || !Number.isInteger(c.revision));
    if (legacy) {
      // Never overwrite the original migration evidence on subsequent starts.
      try { await fs.copyFile(path.join(this.directory,'conversations.json'), path.join(this.directory,'conversations.pre-projects.json'), 1); }
      catch(error) { if(error.code !== 'EEXIST') throw error; }
    }
    if (!this.projects.some(p => p.id === 'default')) this.projects.unshift(this.projectRecord('default','默认项目'));
    for (const c of this.conversations) {
      if (!c.projectId) { c.projectId='default'; c.artifactDirectory ??= path.join(this.directory,'artifacts'); }
      c.pinned ??= false; c.archivedAt ??= null; c.revision ??= 1;
      c.createdAt ??= c.updatedAt;
    }
    await this.save('projects');
    if (legacy) await this.save('conversations');
  }
  async save(key) {
    const snapshot = JSON.stringify(this[key], null, 2);
    const pending = (this.writes.get(key) ?? Promise.resolve()).catch(() => {}).then(async () => {
      const target = path.join(this.directory, `${key}.json`), temp = `${target}.${randomUUID()}.tmp`;
      await fs.writeFile(temp, snapshot, { mode: 0o600 }); await fs.rename(temp, target);
    });
    this.writes.set(key, pending);
    return pending;
  }
  projectRecord(id,name) { const now=new Date().toISOString(); return {id,name,description:'',tiaProjectPath:null,createdAt:now,updatedAt:now,archivedAt:null,revision:1}; }
  project(id) { const p=this.projects.find(p=>p.id===id); if(!p) throw new Error('项目不存在'); return p; }
  createProject(input={}) { const p=this.projectRecord(randomUUID(),'新项目'); applyProject(p,input); this.projects.push(p); return p; }
  updateProject(id,input) { const p=this.project(id); checkRevision(p,input); const next={...p}; applyProject(next,input); Object.assign(p,next,{revision:p.revision+1,updatedAt:new Date().toISOString()}); return p; }
  create(title = '新对话', projectId = 'default') { this.project(projectId); const now=new Date().toISOString(); const c = { id: randomUUID(), projectId, title: String(title).slice(0,80), pinned:false, archivedAt:null, revision:1, createdAt:now, updatedAt:now, messages: [] }; this.conversations.unshift(c); return c; }
  update(id,input) {
    const c=this.get(id); checkRevision(c,input); const next={...c};
    if(input.title!==undefined) next.title=text(input.title,'title',80);
    if(input.projectId!==undefined) { this.project(input.projectId); if(input.projectId!==c.projectId) next.artifactDirectory=this.artifactDirectory(c); next.projectId=input.projectId; }
    if(input.pinned!==undefined) { if(typeof input.pinned!=='boolean') throw new Error('pinned 必须为布尔值'); next.pinned=input.pinned; }
    if(input.archivedAt!==undefined) next.archivedAt=archiveDate(input.archivedAt);
    Object.assign(c,next,{revision:c.revision+1,updatedAt:new Date().toISOString()}); return c;
  }
  artifactDirectory(c) { return c.artifactDirectory || path.join(this.directory,'projects',c.projectId,'designs',c.id); }
  get(id) { const c = this.conversations.find(c => c.id === id); if (!c) throw new Error('会话不存在'); return c; }
}
export function conflict(message='revision 冲突，请重新加载') { const error=new Error(message); error.status=409; return error; }
export function checkRevision(record,input) { if(!Number.isInteger(input.revision) || input.revision!==record.revision) throw conflict(); }
function text(value,name,max) { if(typeof value!=='string'||!value.trim()||value.length>max) throw new Error(`${name} 必须为 1–${max} 字符文本`); return value.trim(); }
function archiveDate(value) { if(value===null) return null; if(typeof value!=='string'||!Number.isFinite(Date.parse(value))) throw new Error('archivedAt 必须为时间字符串或 null'); return new Date(value).toISOString(); }
function applyProject(p,input) {
  if(input.name!==undefined) p.name=text(input.name,'name',80);
  if(input.description!==undefined) { if(typeof input.description!=='string'||input.description.length>4000) throw new Error('description 最多 4000 字符'); p.description=input.description; }
  if(input.archivedAt!==undefined) p.archivedAt=archiveDate(input.archivedAt);
  if(input.tiaProjectPath!==undefined) { if(input.tiaProjectPath!==null && (typeof input.tiaProjectPath!=='string'||!input.tiaProjectPath.trim())) throw new Error('tiaProjectPath 必须为路径或 null'); p.tiaProjectPath=input.tiaProjectPath?.trim() ?? null; }
}
