import http from 'node:http';
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { verifyProjectContext } from './project-context.mjs';
import { Store, conflict, checkRevision, publicConfig, validateConfig } from './store.mjs';
import { McpClient } from './mcp.mjs';
import { completion } from './model.mjs';
import { promptSettings } from './prompts.mjs';
import { effect, runChat } from './agent.mjs';
import { createUpdateChecker } from './updates.mjs';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const appMetadata=JSON.parse((await fs.readFile(path.join(root,'package.json'),'utf8')).replace(/^\uFEFF/,''));
const appVersion=appMetadata.version;
export async function createTaiServer({directory=process.env.TAI_DATA_DIR || path.join(process.env.LOCALAPPDATA || os.homedir(),'TAI'),mcpClient=new McpClient(),modelCompletion=completion,updateChecker=createUpdateChecker({metadata:appMetadata}),port=Number(process.env.TAI_PORT || 17821)}={}) {
const store=new Store(directory);
await store.init();
const mcp=mcpClient;
let active=null;
const json=(res,status,value)=>{res.writeHead(status,{'Content-Type':'application/json; charset=utf-8','Cache-Control':'no-store'});res.end(JSON.stringify(value));};
async function body(req) { const chunks=[]; let size=0; for await(const chunk of req) { size+=chunk.length; if(size>2_500_000) throw new Error('请求过大'); chunks.push(chunk); } const text=Buffer.concat(chunks).toString('utf8'); return text ? JSON.parse(text) : {}; }
async function exclusive(fn,context={}) { if(active) throw conflict('另一个任务正在执行，请等待或取消'); const task={taskId:randomUUID(),conversationId:null,projectId:null,kind:'operation',startedAt:new Date().toISOString(),...context,controller:new AbortController()}; active=task; try{return await fn(task.controller.signal);} finally{if(active===task)active=null;} }
const publicTask=()=>active ? (({controller,...rest})=>rest)(active) : null;
const assertIdle=()=>{if(active) throw conflict('另一个任务正在执行，请等待或取消');};
const assertConversationIdle=id=>{if(active?.conversationId===id)throw conflict('该设计正在运行');};
const assertProjectIdle=id=>{if(active?.projectId===id)throw conflict('该项目的设计正在运行');};
const server=http.createServer(async(req,res)=>{
  try {
    const host=req.headers.host,port=server.address()?.port;
    if(![`127.0.0.1:${port}`,`localhost:${port}`].includes(host)) return json(res,403,{error:'无效的本地请求主机'});
    if(req.headers.origin && ![`http://127.0.0.1:${port}`,`http://localhost:${port}`,'http://localhost:5173','http://127.0.0.1:5173'].includes(req.headers.origin)) return json(res,403,{error:'拒绝跨站请求'});
    if(req.headers['sec-fetch-site']==='cross-site') return json(res,403,{error:'拒绝跨站请求'});
    const url=new URL(req.url,`http://${host}`), p=url.pathname, method=req.method;
    if(p.startsWith('/api/') && ['POST','PUT','DELETE'].includes(method) && method!=='DELETE' && !req.headers['content-type']?.startsWith('application/json')) return json(res,415,{error:'请求必须使用 application/json'});
    if(p==='/api/status') return json(res,200,{app:'TAI',version:appVersion,mcp:{running:Boolean(mcp.ready),connected:mcp.connected,toolCount:mcp.tools.length},model:{configured:Boolean(store.config.model),provider:store.config.provider,model:store.config.model},busy:Boolean(active),activeTask:publicTask()});
    if(p==='/api/updates' && method==='GET') return json(res,200,await updateChecker({refresh:url.searchParams.get('refresh')==='1'}));
    if(p==='/api/prompts' && method==='GET') return json(res,200,promptSettings(store.config));
    if(p==='/api/prompts' && method==='PUT') return json(res,200,await exclusive(async()=>{
      const input=await body(req);
      if(typeof input.systemPrompt!=='string') throw new Error('必须提供 systemPrompt 文本');
      const next=validateConfig({systemPrompt:input.systemPrompt},store.config);
      const previous=store.config; store.config=next;
      try {await store.save('config');} catch(error) {store.config=previous;throw error;}
      return promptSettings(store.config);
    }));
    if(p==='/api/config' && method==='GET') return json(res,200,publicConfig(store.config));
    if(p==='/api/config' && method==='PUT') return json(res,200,await exclusive(async()=>{const input=await body(req);const next=validateConfig(input,store.config);const changed=['mcpPath','tiaLocation','tiaVersion'].some(k=>next[k]!==store.config[k]);store.config=next;await store.save('config');if(changed)mcp.stop();return publicConfig(next);}));
    if(p==='/api/model/test' && method==='POST') return json(res,200,await exclusive(async signal=>{const r=await modelCompletion(store.config,[{role:'user',content:'请只回复：TAI 模型连接成功'}],[],signal);return {ok:true,text:r.content};},{kind:'model-test'}));
    if(p==='/api/chat/cancel' && method==='POST') {const data=await body(req);if(data.taskId!==undefined){if(!active||data.taskId!==active.taskId)throw conflict('任务已结束或 taskId 不匹配');active.controller.abort();}else if(active?.kind==='chat')active.controller.abort();return json(res,200,{ok:true});}
    if(p==='/api/tools' && method==='GET') return json(res,200,await exclusive(async()=>{await mcp.start(store.config);return {tools:mcp.tools.map(t=>({...t,effect:effect(t.name)}))};}));
    if(p==='/api/tia/state' && method==='GET') return json(res,200,await exclusive(async()=>{await mcp.start(store.config);return mcp.call('GetState');}));
    if(['/api/tia/connect','/api/tia/disconnect','/api/tools/call'].includes(p) && method==='POST') return json(res,200,await exclusive(async()=>{
      const data=await body(req);await mcp.start(store.config);
      const name=p==='/api/tools/call'?data.name:p.endsWith('/connect')?'Connect':'Disconnect';
      if(!mcp.tools.some(t=>t.name===name)) throw new Error('未知的 MCP 工具');
      if(!['Connect','Disconnect','GetState','GetCompatibilityInfo'].includes(name)) await verifyProjectContext(mcp,data.projectId ? store.project(data.projectId) : null);
      return mcp.call(name,data.arguments??{});
    }));
    if(p==='/api/projects' && method==='GET') return json(res,200,{projects:store.projects});
    if(p==='/api/projects' && method==='POST') {
      const data=await body(req);
      if(data.tiaProjectPath!=null) await exclusive(()=>verifyProjectContext(mcp,{tiaProjectPath:data.tiaProjectPath}),{kind:'binding'});
      const project=store.createProject(data);await store.save('projects');return json(res,201,project);
    }
    const projectMatch=p.match(/^\/api\/projects\/([a-zA-Z0-9-]+)$/);
    if(projectMatch && method==='PUT') {
      const data=await body(req),project=store.project(projectMatch[1]);checkRevision(project,data);
      if(data.tiaProjectPath!==undefined) {
        assertProjectIdle(project.id);assertIdle();
        if(data.tiaProjectPath!==null) await exclusive(()=>verifyProjectContext(mcp,{tiaProjectPath:data.tiaProjectPath}),{projectId:project.id,kind:'binding'});
        assertProjectIdle(project.id);checkRevision(project,data);
      }
      const updated=store.updateProject(project.id,data);await store.save('projects');return json(res,200,updated);
    }
    if(p==='/api/conversations' && method==='GET') {
      let conversations=store.conversations;
      if(url.searchParams.has('projectId'))conversations=conversations.filter(c=>c.projectId===url.searchParams.get('projectId'));
      if(url.searchParams.has('search')) {const search=url.searchParams.get('search').toLocaleLowerCase();conversations=conversations.filter(c=>c.title.toLocaleLowerCase().includes(search));}
      const archived=url.searchParams.get('archived');
      if(archived==='true'||archived==='1')conversations=conversations.filter(c=>Boolean(c.archivedAt));
      if(archived==='false'||archived==='0')conversations=conversations.filter(c=>!c.archivedAt);
      if(url.searchParams.get('summary')==='1')conversations=conversations.map(({messages,artifactDirectory,...c})=>({...c,messageCount:messages.length}));
      return json(res,200,{conversations});
    }
    if(p==='/api/conversations' && method==='POST') {const data=await body(req);const c=store.create(data.title,data.projectId);await store.save('conversations');return json(res,201,c);}
    const match=p.match(/^\/api\/conversations\/([a-f0-9-]+)$/);
    if(match && method==='GET') return json(res,200,store.get(match[1]));
    if(match && method==='PUT') {
      const data=await body(req),c=store.get(match[1]);
      if(data.projectId!==undefined&&data.projectId!==c.projectId)assertConversationIdle(c.id);
      const updated=store.update(c.id,data);await store.save('conversations');return json(res,200,updated);
    }
    if(match && method==='DELETE') {assertConversationIdle(match[1]);store.get(match[1]);store.conversations=store.conversations.filter(c=>c.id!==match[1]);await store.save('conversations');return json(res,200,{ok:true});}
    if(p==='/api/chat' && method==='POST') {
      if(active)return json(res,409,{error:'另一个任务正在执行'});
      const data=await body(req);
      if(typeof data.message!=='string' || !data.message.trim() || data.message.length>40000) throw new Error('请输入 1–40000 字符的消息');
      if(!['read','write'].includes(data.mode))throw new Error('无效的工作模式');
      const conversation=store.get(data.conversationId);
      return await exclusive(async signal=>{
        res.writeHead(200,{'Content-Type':'application/x-ndjson; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});
        res.write(JSON.stringify({type:'task',...publicTask()})+'\n');
        const controller=active.controller;
        const close=()=>{if(!res.writableEnded)controller.abort();};res.on('close',close);
        try{await runChat({store,mcp,conversation,project:store.project(conversation.projectId),modelCompletion,message:data.message.trim(),mode:data.mode,signal,artifactDirectory:store.artifactDirectory(conversation),emit:e=>{if(!res.destroyed)res.write(JSON.stringify(e)+'\n');}});}finally{res.off('close',close);res.end();}
      },{conversationId:conversation.id,projectId:conversation.projectId,kind:'chat'});
    }
    if(p.startsWith('/api/'))return json(res,404,{error:'接口不存在'});
    if(method!=='GET' && method!=='HEAD')return json(res,405,{error:'不支持此方法'});
    const dist=path.join(root,'frontend','dist');
    const relative=decodeURIComponent(p).replace(/^\/+/,''), target=path.resolve(dist,relative || 'index.html');
    if(!target.startsWith(dist+path.sep))return json(res,403,{error:'无效路径'});
    let file=target;
    try {if(!(await fs.stat(file)).isFile())file=path.join(dist,'index.html');}catch{file=path.join(dist,'index.html');}
    let data;try{data=await fs.readFile(file);}catch{return json(res,503,{error:'前端尚未构建。请运行 npm --prefix frontend install 和 npm run build。'});}
    const types={'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.svg':'image/svg+xml','.png':'image/png','.ico':'image/x-icon'};
    res.writeHead(200,{'Content-Type':types[path.extname(file)] || 'application/octet-stream','X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer','Content-Security-Policy':"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'"});res.end(method==='HEAD'?undefined:data);
  }catch(error){if(!res.headersSent)json(res,error.status || 400,{error:error.message});else res.end();}
});
return {server,store,mcp,get activeTask(){return publicTask();},close(){active?.controller.abort();mcp.stop();return new Promise(resolve=>{server.closeAllConnections();server.close(resolve);});}};
}
if(process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
const port=Number(process.env.TAI_PORT || 17821);
const app=await createTaiServer({port});
const {server,mcp}=app;
server.listen(port,'127.0.0.1',()=>{
  const url=`http://127.0.0.1:${port}`;
  console.log(`TAI ready: ${url}`);
  if(process.env.TAI_OPEN==='1' && process.platform==='win32') {
    const browser=spawn('explorer.exe',[url],{stdio:'ignore',windowsHide:true});
    browser.on('error',()=>console.log(`请在浏览器打开 ${url}`));
  }
});
server.on('error',error=>{console.error(error.message);process.exitCode=1;});
function shutdown(){app.close();setTimeout(()=>process.exit(0),1500).unref();}
process.on('SIGINT',shutdown);process.on('SIGTERM',shutdown);
}
