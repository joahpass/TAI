import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { Store } from '../server/store.mjs';
import { createTaiServer } from '../server/index.mjs';
import { runChat } from '../server/agent.mjs';

async function temporary(t) {
  const directory=await fs.mkdtemp(path.join(os.tmpdir(),'tai-workspace-offline-'));
  t.after(()=>fs.rm(directory,{recursive:true,force:true}));
  return directory;
}
function disconnectedMcp() {
  return {ready:false,connected:false,tools:[],async start(){throw new Error('Offline test must never start MCP');},async call(){throw new Error('Offline test must never call MCP');},stop(){}};
}
async function appFor(t,modelCompletion) {
  const app=await createTaiServer({directory:await temporary(t),port:0,mcpClient:disconnectedMcp(),modelCompletion});
  await new Promise(resolve=>app.server.listen(0,'127.0.0.1',resolve));
  t.after(()=>app.close());
  const base=`http://127.0.0.1:${app.server.address().port}`;
  const request=async(route,method='GET',data)=>{
    const response=await fetch(base+route,{method,headers:data===undefined?{}:{'Content-Type':'application/json'},body:data===undefined?undefined:JSON.stringify(data)});
    const text=await response.text();
    return {status:response.status,text,data:()=>JSON.parse(text)};
  };
  return {app,request};
}
function gate(){let release;const promise=new Promise(resolve=>release=resolve);return {promise,release};}

test('project instructions migration preserves exact originals and history, is idempotent',async t=>{
  const directory=await temporary(t);
  const projects=[{id:'default',name:'Original',description:'Keep',tiaProjectPath:'C:/Keep.ap19',revision:7,createdAt:'2024-01-01',updatedAt:'2024-01-02',archivedAt:null},{id:'configured',name:'Already configured',instructions:'Keep instructions',revision:3}];
  const conversations=[{id:'history',projectId:'default',title:'History',artifactDirectory:'C:/Existing/artifacts',revision:9,messages:[{role:'user',content:'Existing history'}]}];
  const original=JSON.stringify(projects,null,4);
  await fs.writeFile(path.join(directory,'projects.json'),original);
  await fs.writeFile(path.join(directory,'conversations.json'),JSON.stringify(conversations));
  const store=new Store(directory);await store.init();
  assert.equal(await fs.readFile(path.join(directory,'projects.pre-instructions.json'),'utf8'),original);
  assert.deepEqual(store.projects,[{...projects[0],instructions:''},projects[1]]);
  assert.equal(store.get('history').revision,9);
  assert.equal(store.artifactDirectory(store.get('history')),'C:/Existing/artifacts');
  assert.deepEqual(store.get('history').messages,conversations[0].messages);
  store.updateProject('default',{revision:7,instructions:'New instructions'});await store.save('projects');
  const again=new Store(directory);await again.init();
  assert.deepEqual(again.projects,store.projects);
  assert.equal(await fs.readFile(path.join(directory,'projects.pre-instructions.json'),'utf8'),original);
  assert.deepEqual(again.get('history').messages,conversations[0].messages);
});

test('instructions API accepts empty and 20000 characters, rejects invalid values without mutation',async t=>{
  const {request}=await appFor(t,async()=>({content:'Done'}));
  assert.equal((await request('/api/projects')).data().projects[0].instructions,'');
  for(const instructions of [null,123,{},[],true,'a\0b','a'.repeat(20001)]){
    assert.equal((await request('/api/projects','POST',{instructions})).status,400);
    assert.equal((await request('/api/projects/default','PUT',{revision:1,instructions})).status,400);
  }
  assert.equal((await request('/api/projects')).data().projects.length,1);
  const value=' '+ 'a'.repeat(19998)+' ';
  const updated=await request('/api/projects/default','PUT',{revision:1,instructions:value});
  assert.equal(updated.status,200);assert.equal(updated.data().instructions,value);
  assert.equal((await request('/api/projects/default','PUT',{revision:2,instructions:''})).data().instructions,'');
  const created=(await request('/api/projects','POST',{instructions:'Project rules'})).data();
  assert.equal(created.instructions,'Project rules');
});

test('archived projects reject new conversations, moves and chats; archived conversations reject chats',async t=>{
  let calls=0;const {request}=await appFor(t,async()=>{calls++;return {content:'Done'};});
  const c=(await request('/api/conversations','POST',{})).data();
  const p=(await request('/api/projects','POST',{name:'Archived',archivedAt:'2025-01-01'})).data();
  assert.equal((await request('/api/conversations','POST',{projectId:p.id})).status,409);
  assert.equal((await request(`/api/conversations/${c.id}`,'PUT',{revision:c.revision,projectId:p.id})).status,409);
  assert.equal((await request('/api/projects/default','PUT',{revision:1,archivedAt:'2025-01-01'})).status,200);
  assert.equal((await request('/api/chat','POST',{conversationId:c.id,message:'Blocked',mode:'read'})).status,409);
  assert.equal((await request('/api/projects/default','PUT',{revision:2,archivedAt:null})).status,200);
  assert.equal((await request(`/api/conversations/${c.id}`,'PUT',{revision:c.revision,archivedAt:'2025-01-01'})).status,200);
  assert.equal((await request('/api/chat','POST',{conversationId:c.id,message:'Blocked',mode:'read'})).status,409);
  assert.equal(calls,0);
  const saved=(await request(`/api/conversations/${c.id}`)).data();assert.deepEqual(saved.messages,[]);
  assert.equal(saved.projectId,'default');
  assert.equal((await request(`/api/conversations/${c.id}`,'PUT',{revision:saved.revision,archivedAt:null})).status,200);
  assert.equal((await request('/api/chat','POST',{conversationId:c.id,message:'Restored',mode:'read'})).status,200);
  assert.equal(calls,1);
});

test('active task blocks related archives but permits instructions edits; next task sees edits and isolated history',async t=>{
  const entered=gate(),resume=gate(),seen=[];
  const {request}=await appFor(t,async(config,messages)=>{
    seen.push(structuredClone(messages));
    if(seen.length===1){entered.release();await resume.promise;}
    return {content:'Offline reply'};
  });
  const p=(await request('/api/projects','POST',{name:'Other project',instructions:'Other rules'})).data();
  await request('/api/projects/default','PUT',{revision:1,description:'Old description',instructions:'Old rules'});
  const a=(await request('/api/conversations','POST',{})).data();
  const b=(await request('/api/conversations','POST',{})).data();
  assert.equal(a.title,'新对话');
  const pending=request('/api/chat','POST',{conversationId:a.id,message:'Private first message',mode:'read'});
  await entered.promise;
  try {
    const current=(await request(`/api/conversations/${a.id}`)).data();
    assert.equal((await request(`/api/conversations/${a.id}`,'PUT',{revision:current.revision,archivedAt:'2025-01-01'})).status,409);
    assert.equal((await request('/api/projects/default?revision=2','DELETE')).status,409);
    assert.equal((await request(`/api/conversations/${a.id}?revision=${current.revision}`,'DELETE')).status,409);
    assert.equal((await request('/api/projects/default','PUT',{revision:2,archivedAt:'2025-01-01'})).status,409);
    assert.equal((await request('/api/projects/default','PUT',{revision:2,description:'New description',instructions:'New rules'})).status,200);
    assert.equal((await request(`/api/projects/${p.id}`,'PUT',{revision:p.revision,archivedAt:'2025-01-01'})).status,200);
    assert.equal((await request(`/api/conversations/${b.id}`,'PUT',{revision:b.revision,archivedAt:'2025-01-01'})).status,200);
    assert.match(seen[0][1].content,/Old rules/);assert.doesNotMatch(seen[0][1].content,/New rules/);
  } finally {resume.release();await pending;}
  await request('/api/chat','POST',{conversationId:a.id,message:'Second message',mode:'read'});
  assert.match(seen[1][1].content,/New rules/);assert.match(seen[1][1].content,/New description/);
  assert.ok(seen[1].some(m=>m.content==='Private first message'));
  const fresh=(await request('/api/conversations','POST',{})).data();
  await request('/api/chat','POST',{conversationId:fresh.id,message:'Fresh message',mode:'read'});
  assert.equal(seen[2].some(m=>m.content==='Private first message'),false);
  assert.equal((await request(`/api/conversations/${a.id}`)).data().title,'Private first message');
  const other=(await request('/api/projects','POST',{instructions:'Isolated rules'})).data();
  const isolated=(await request('/api/conversations','POST',{projectId:other.id})).data();
  await request('/api/chat','POST',{conversationId:isolated.id,message:'Other message',mode:'read'});
  assert.match(seen[3][1].content,/Isolated rules/);assert.doesNotMatch(JSON.stringify(seen[3]),/New rules|Private first message/);
});

test('running non-default project rejects deletion and archive until task finishes',async t=>{
  const entered=gate(),resume=gate();
  const {request}=await appFor(t,async()=>{entered.release();await resume.promise;return {content:'Done'};});
  const p=(await request('/api/projects','POST',{name:'Running project'})).data();
  const c=(await request('/api/conversations','POST',{projectId:p.id})).data();
  const pending=request('/api/chat','POST',{conversationId:c.id,message:'Hold',mode:'read'});
  await entered.promise;
  try {
    assert.equal((await request(`/api/projects/${p.id}?revision=1`,'DELETE')).status,409);
    assert.equal((await request(`/api/projects/${p.id}`,'PUT',{revision:1,archivedAt:'2025-01-01'})).status,409);
    assert.equal((await request(`/api/projects/${p.id}`,'PUT',{revision:1,instructions:'Can edit while running'})).status,200);
    assert.equal((await request(`/api/projects/${p.id}?revision=2`,'DELETE')).status,409);
  } finally {resume.release();await pending;}
  assert.equal((await request(`/api/projects/${p.id}`,'PUT',{revision:2,archivedAt:'2025-01-01'})).status,200);
  assert.equal((await request(`/api/projects/${p.id}?revision=3`,'DELETE')).status,200);
});

test('project and conversation deletion require revisions, cascade records and retain disk artifacts',async t=>{
  const {app,request}=await appFor(t,async()=>({content:'Done'}));
  const p=(await request('/api/projects','POST',{name:'Delete me'})).data();
  const a=(await request('/api/conversations','POST',{projectId:p.id})).data();
  const b=(await request('/api/conversations','POST',{projectId:p.id})).data();
  const keep=(await request('/api/conversations','POST',{})).data();
  const directory=app.store.artifactDirectory(a);await fs.mkdir(directory,{recursive:true});
  await fs.writeFile(path.join(directory,'retained.txt'),'Retained artifact');
  assert.equal((await request(`/api/projects/${p.id}`,'DELETE')).status,409);
  assert.equal((await request(`/api/projects/${p.id}?revision=99`,'DELETE')).status,409);
  assert.equal((await request('/api/projects/default?revision=1','DELETE')).status,409);
  const deleted=await request(`/api/projects/${p.id}?revision=${p.revision}`,'DELETE');
  assert.equal(deleted.status,200);
  assert.deepEqual(deleted.data(),{ok:true,projectId:p.id,conversationIds:[b.id,a.id]});
  assert.equal(await fs.readFile(path.join(directory,'retained.txt'),'utf8'),'Retained artifact');
  assert.deepEqual((await request('/api/conversations')).data().conversations.map(c=>c.id),[keep.id]);
  const restored=new Store(app.store.directory);await restored.init();
  assert.equal(restored.projects.some(project=>project.id===p.id),false);
  assert.deepEqual(restored.conversations.map(c=>c.id),[keep.id]);
  assert.equal((await request(`/api/conversations/${keep.id}`,'DELETE')).status,409);
  assert.equal((await request(`/api/conversations/${keep.id}?revision=9`,'DELETE')).status,409);
  assert.deepEqual((await request(`/api/conversations/${keep.id}?revision=${keep.revision}`,'DELETE')).data(),{ok:true,conversationId:keep.id});
});

test('runChat snapshots project before the first await and keeps both default title generations compatible',async t=>{
  const store=new Store(await temporary(t));await store.init();
  const project=store.project('default');project.instructions='Snapshot rules';
  const saved=store.save.bind(store),entered=gate(),resume=gate();let hold=true;
  store.save=async key=>{if(hold&&key==='conversations'){hold=false;entered.release();await resume.promise;}return saved(key);};
  const c=store.create('新设计'),seen=[];
  const pending=runChat({store,mcp:disconnectedMcp(),conversation:c,project,message:'Generated title',mode:'read',emit(){},signal:new AbortController().signal,artifactDirectory:store.artifactDirectory(c),modelCompletion:async(config,messages)=>{seen.push(messages);return {content:'Done'};}});
  await entered.promise;project.instructions='Edited during save';project.description='Edited description';resume.release();await pending;
  assert.match(seen[0][1].content,/Snapshot rules/);assert.doesNotMatch(seen[0][1].content,/Edited during save|Edited description/);
  assert.equal(c.title,'Generated title');
});
