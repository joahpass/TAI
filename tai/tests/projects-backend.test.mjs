import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import { Store } from '../server/store.mjs';
import { createTaiServer } from '../server/index.mjs';
import { runChat, artifact } from '../server/agent.mjs';
import { normalizeProjectPath, verifyProjectContext } from '../server/project-context.mjs';
const envelope=data=>({content:[{type:'text',text:JSON.stringify(data)}]});
async function temporary(t) { const directory=await fs.mkdtemp(path.join(os.tmpdir(),'tai-projects-'));t.after(()=>fs.rm(directory,{recursive:true,force:true}));return directory; }
function fakeMcp() {return {ready:true,connected:true,tools:[{name:'GetProject',inputSchema:{type:'object'}}],calls:[],state:{IsConnected:true,ProjectPath:'C:\\Engineering\\Main.ap19'},async start(){},stop(){},async call(name,args){this.calls.push({name,args});return envelope(name==='GetState'?this.state:{Name:'Main'});}};}
async function appFor(t,options={}) {
 const app=await createTaiServer({directory:await temporary(t),port:0,mcpClient:fakeMcp(),...options});
 await new Promise(resolve=>app.server.listen(0,'127.0.0.1',resolve));t.after(()=>app.close());
 const base=`http://127.0.0.1:${app.server.address().port}`;
 const request=async(route,method='GET',data)=>{const response=await fetch(base+route,{method,headers:data===undefined?{}:{'Content-Type':'application/json'},body:data===undefined?undefined:JSON.stringify(data)});const text=await response.text();return {status:response.status,text,data:()=>JSON.parse(text)};};
 return {...app,app,base,request};
}
function gate() {let release;const promise=new Promise(resolve=>release=resolve);return {promise,release};}

test('migration backs up originals once, preserves legacy artifacts and revisions',async t=>{
 const directory=await temporary(t),old=[{id:'legacy',title:'Existing',updatedAt:'2024-01-01',messages:[]}];
 const original=JSON.stringify(old);await fs.writeFile(path.join(directory,'conversations.json'),original);
 const first=new Store(directory);await first.init();
 assert.equal(await fs.readFile(path.join(directory,'conversations.pre-projects.json'),'utf8'),original);
 const c=first.get('legacy');assert.equal(c.projectId,'default');assert.equal(c.revision,1);assert.equal(first.artifactDirectory(c),path.join(directory,'artifacts'));
 const second=new Store(directory);await second.init();assert.deepEqual(second.conversations,first.conversations);assert.equal(second.projects.length,1);
 assert.equal(await fs.readFile(path.join(directory,'conversations.pre-projects.json'),'utf8'),original);
 const a=second.create('A'),p=second.createProject({name:'P'}),b=second.create('B',p.id);
 assert.equal(second.artifactDirectory(a),path.join(directory,'projects','default','designs',a.id));
 assert.notEqual(second.artifactDirectory(a),second.artifactDirectory(b));
 await artifact(second.artifactDirectory(a),'TAI_WriteArtifact',{name:'same.scl',content:'Project A'});
 await artifact(second.artifactDirectory(b),'TAI_WriteArtifact',{name:'same.scl',content:'Project B'});
 assert.equal(await fs.readFile(path.join(second.artifactDirectory(a),'same.scl'),'utf8'),'Project A');
 assert.equal(await fs.readFile(path.join(second.artifactDirectory(b),'same.scl'),'utf8'),'Project B');
 const initial=second.artifactDirectory(a);second.update(a.id,{revision:a.revision,projectId:p.id});assert.equal(second.artifactDirectory(a),initial);
});

test('project and conversation metadata contracts, conflicts and summaries',async t=>{
 const {request}=await appFor(t);
 const p=(await request('/api/projects','POST',{name:'Project A',description:'Description'})).data();assert.equal(p.revision,1);assert.equal(p.tiaProjectPath,null);
 const c=(await request('/api/conversations','POST',{title:'Design α',projectId:p.id})).data();assert.equal(c.projectId,p.id);
 assert.equal((await request(`/api/conversations/${c.id}`,'PUT',{title:'Missing revision'})).status,409);
 const updated=(await request(`/api/conversations/${c.id}`,'PUT',{revision:1,title:'Renamed',pinned:true,archivedAt:'2025-01-01T00:00:00Z'})).data();assert.equal(updated.revision,2);
 assert.equal((await request(`/api/conversations/${c.id}`,'PUT',{revision:1,pinned:false})).status,409);
 const summaries=(await request(`/api/conversations?summary=1&projectId=${p.id}&search=rename&archived=true`)).data().conversations;
 assert.equal(summaries.length,1);assert.equal(summaries[0].messages,undefined);assert.equal(summaries[0].messageCount,0);
 assert.equal((await request('/api/conversations?summary=1&archived=false')).data().conversations.length,0);
 assert.ok(Array.isArray((await request('/api/conversations')).data().conversations[0].messages));
 assert.equal((await request(`/api/conversations/${c.id}`,'PUT',{revision:2,archivedAt:null,projectId:'default'})).status,200);
 assert.equal((await request(`/api/projects/${p.id}`,'PUT',{revision:1,name:'Project B'})).data().revision,2);
 assert.equal((await request(`/api/projects/${p.id}`,'PUT',{revision:1,name:'Stale'})).status,409);
 assert.equal((await request(`/api/projects/${p.id}`,'DELETE')).status,409);
});

test('binding is verified against live GetState without starting TIA',async t=>{
 const {request,mcp}=await appFor(t);const p=(await request('/api/projects','GET')).data().projects[0];
 assert.equal((await request(`/api/projects/${p.id}`,'PUT',{revision:p.revision,tiaProjectPath:'c:/Engineering/../Engineering/MAIN.ap19'})).status,200);
 assert.equal(mcp.calls.filter(c=>c.name==='GetState').length,1);
 assert.equal((await request(`/api/projects/${p.id}`,'PUT',{revision:2,tiaProjectPath:'C:/Wrong.ap19'})).status,400);
 const saved=(await request('/api/projects')).data().projects[0];assert.equal(saved.revision,2);
 assert.equal((await request('/api/tools/call','POST',{name:'GetProject',projectId:p.id})).status,200);
 mcp.state={IsConnected:false};assert.equal((await request('/api/tools/call','POST',{name:'GetProject',projectId:p.id})).status,400);
 assert.equal(mcp.calls.filter(c=>c.name==='GetProject').length,1);
});

test('live path normalization rejects absent, unknown, disconnected and mismatching contexts',async()=>{
 assert.equal(normalizeProjectPath('C:/Foo/../BAR/'),normalizeProjectPath('c:\\bar'));
 const mcp=fakeMcp();await assert.rejects(()=>verifyProjectContext(mcp,null),/未绑定/);
 mcp.state={IsConnected:true};await assert.rejects(()=>verifyProjectContext(mcp,{tiaProjectPath:'C:/X'}),/路径未知/);
 mcp.state={IsConnected:false,ProjectPath:'C:/X'};await assert.rejects(()=>verifyProjectContext(mcp,{tiaProjectPath:'C:/X'}),/未连接/);
 mcp.state={IsConnected:true,ProjectPath:'C:/Y'};await assert.rejects(()=>verifyProjectContext(mcp,{tiaProjectPath:'C:/X'}),/不匹配/);
});

test('each model engineering call is guarded; pure model and artifacts run unbound',async t=>{
 const store=new Store(await temporary(t));await store.init();const c=store.create(),project=store.project('default'),mcp=fakeMcp();
 let step=0;const events=[];const modelCompletion=async()=>step++===0?{content:'',tool_calls:[{id:'1',function:{name:'GetProject',arguments:'{}'}}]}:{content:'Completed'};
 await runChat({store,mcp,conversation:c,project,message:'Inspect',mode:'read',signal:new AbortController().signal,artifactDirectory:store.artifactDirectory(c),modelCompletion,emit:e=>events.push(e)});
 assert.equal(mcp.calls.length,0);assert.match(events.find(e=>e.type==='tool_end').result.content[0].text,/上下文错误/);
 project.tiaProjectPath=mcp.state.ProjectPath;step=0;events.length=0;
 await runChat({store,mcp,conversation:c,project,message:'Inspect',mode:'read',signal:new AbortController().signal,artifactDirectory:store.artifactDirectory(c),modelCompletion,emit:e=>events.push(e)});
 assert.deepEqual(mcp.calls.map(c=>c.name),['GetState','GetProject']);
 const originalCall=mcp.call.bind(mcp);mcp.call=async(name,args)=>{const result=await originalCall(name,args);if(name==='GetProject')mcp.state={IsConnected:true,ProjectPath:'C:/Switched.ap19'};return result;};step=0;
 await runChat({store,mcp,conversation:c,project,message:'Two calls',mode:'read',signal:new AbortController().signal,artifactDirectory:store.artifactDirectory(c),modelCompletion:async()=>step++===0?{content:'',tool_calls:[{id:'a',function:{name:'GetProject',arguments:'{}'}},{id:'b',function:{name:'GetProject',arguments:'{}'}}]}:{content:'Done'},emit:e=>events.push(e)});
 assert.deepEqual(mcp.calls.map(c=>c.name),['GetState','GetProject','GetState','GetProject','GetState']);
 assert.match(events.filter(e=>e.type==='tool_end').at(-1).result.content[0].text,/不匹配/);
 project.tiaProjectPath=null;step=0;
 await runChat({store,mcp,conversation:c,project,message:'List',mode:'read',signal:new AbortController().signal,artifactDirectory:store.artifactDirectory(c),modelCompletion:async()=>step++===0?{content:'',tool_calls:[{id:'2',function:{name:'TAI_ListArtifacts',arguments:'{}'}}]}:{content:'Done'},emit:e=>events.push(e)});
 assert.equal(mcp.calls.length,5);
});

test('active task, revision updates while running, related locks and taskId cancellation',async t=>{
 const entered=gate();const {request,app}=await appFor(t,{modelCompletion:async(config,messages,tools,signal)=>{entered.release();await new Promise((resolve,reject)=>signal.addEventListener('abort',()=>reject(signal.reason),{once:true}));}});
 const c=(await request('/api/conversations','POST',{})).data(),other=(await request('/api/conversations','POST',{})).data();
 const chat=request('/api/chat','POST',{conversationId:c.id,message:'hold',mode:'read'});await entered.promise;
 const status=(await request('/api/status')).data(),task=status.activeTask;assert.equal(task.kind,'chat');assert.equal(task.conversationId,c.id);assert.equal(task.projectId,'default');assert.equal(task.controller,undefined);
 const current=(await request(`/api/conversations/${c.id}`)).data();assert.equal((await request(`/api/conversations/${c.id}`,'PUT',{revision:current.revision,title:'Live rename',pinned:true})).status,200);
 assert.equal((await request('/api/projects/default','PUT',{revision:1,name:'Live project'})).status,200);
 const moved=(await request(`/api/conversations/${c.id}`)).data();assert.equal((await request(`/api/conversations/${c.id}`,'PUT',{revision:moved.revision,projectId:'bad'})).status,409);
 assert.equal((await request(`/api/conversations/${c.id}?revision=${moved.revision}`,'DELETE')).status,409);
 assert.equal((await request(`/api/conversations/${other.id}?revision=${other.revision}`,'DELETE')).status,200);
 assert.equal((await request('/api/projects/default','PUT',{revision:2,tiaProjectPath:'C:/X'})).status,409);
 assert.equal((await request('/api/chat/cancel','POST',{taskId:'stale'})).status,409);assert.ok(app.activeTask);
 assert.equal((await request('/api/chat/cancel','POST',{taskId:task.taskId})).status,200);await chat;
 const final=(await request(`/api/conversations/${c.id}`)).data();assert.equal(final.title,'Live rename');assert.equal(final.revision,moved.revision+1);assert.equal((await request('/api/status')).data().activeTask,null);
});

test('legacy cancellation leaves a non-chat task running',async t=>{
 const entered=gate();const {request,app}=await appFor(t,{modelCompletion:async(config,messages,tools,signal)=>{entered.release();await new Promise((resolve,reject)=>signal.addEventListener('abort',()=>reject(new Error('cancelled')),{once:true}));}});
 const pending=request('/api/model/test','POST',{});await entered.promise;const task=app.activeTask;
 await request('/api/chat/cancel','POST',{});assert.equal(app.activeTask.taskId,task.taskId);
 await request('/api/chat/cancel','POST',{taskId:task.taskId});assert.equal((await pending).status,400);
});

test('chat rechecks mutual exclusion after waiting for request body',async t=>{
 const entered=gate();const {request,base}=await appFor(t,{modelCompletion:async(config,messages,tools,signal)=>{entered.release();await new Promise((resolve,reject)=>signal.addEventListener('abort',()=>reject(signal.reason),{once:true}));}});
 const c=(await request('/api/conversations','POST',{})).data();const data=JSON.stringify({conversationId:c.id,message:'wait',mode:'read'});
 let finish;const slow=new Promise((resolve,reject)=>{const req=http.request(base+'/api/chat',{method:'POST',headers:{'Content-Type':'application/json'}},res=>{res.resume();res.on('end',()=>resolve(res.statusCode));});req.on('error',reject);req.write(data.slice(0,5));finish=()=>req.end(data.slice(5));});
 const chat=request('/api/chat','POST',{conversationId:c.id,message:'first',mode:'read'});await entered.promise;finish();assert.equal(await slow,409);
 await request('/api/chat/cancel','POST',{});await chat;
});

test('chat task event identifies the original design and preserves a custom title',async t=>{
 const {request}=await appFor(t,{modelCompletion:async()=>({content:'Done'})});
 const c=(await request('/api/conversations','POST',{title:'Manual design'})).data();
 const chat=await request('/api/chat','POST',{conversationId:c.id,message:'Different request',mode:'read'});
 const first=JSON.parse(chat.text.split('\n')[0]);
 assert.equal(first.type,'task');assert.equal(first.conversationId,c.id);assert.equal(first.projectId,'default');assert.ok(first.taskId);assert.equal(first.controller,undefined);
 const saved=(await request(`/api/conversations/${c.id}`)).data();assert.equal(saved.title,'Manual design');assert.equal(saved.messages[0].mode,'read');
});
