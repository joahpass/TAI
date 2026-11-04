import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { defaults, Store, validateConfig } from '../server/store.mjs';
import { runChat, permitted, permissionModes } from '../server/agent.mjs';
import { requestBody, completion, endpoint } from '../server/model.mjs';
import { createModelDiscovery } from '../server/models.mjs';
import { validateChatInput, resolveChatSelection } from '../server/chat-options.mjs';
import { createTaiServer } from '../server/index.mjs';
const mcp=()=>({ready:false,connected:false,tools:[],async start(){throw Error('No real MCP');},async call(){throw Error('No real TIA');},stop(){}});
async function temp(t){const d=await fs.mkdtemp(path.join(os.tmpdir(),'tai-selection-'));t.after(()=>fs.rm(d,{recursive:true,force:true}));return d;}
const conf=(v={})=>({...defaults,model:'mock-model',...v});
const user=[{role:'user',content:'Hello'}];
const gate=()=>{let release;const promise=new Promise(r=>release=r);return {promise,release};};

test('permission matrix denies unknown tools and gives local writes only at artifact level',()=>{
 assert.deepEqual(permissionModes.map(m=>m.label),['只读','工作区内修改','完全权限']);
 for(const mode of ['read','artifact','write']){
  assert.equal(permitted('GetState',mode),true);assert.equal(permitted('TAI_ReadArtifact',mode),true);
  assert.equal(permitted('TAI_WriteArtifact',mode),mode!=='read');assert.equal(permitted('TAI_EditArtifact',mode),mode!=='read');
  for(const n of ['ImportBlock','SaveProject','Connect','OpenProject','CloseProject','Disconnect'])assert.equal(permitted(n,mode),mode==='write');
  assert.equal(permitted('Unlisted',mode),false);
 }
 assert.equal(permitted('GetState','unknown'),false);
});
test('shared validation rejects unknown fields, malformed IDs and insufficient manual thinking budget',()=>{
 const input={conversationId:'c',message:'x',mode:'artifact'};
 for(const model of ['', ' ',null,1,'x'.repeat(201),'a\0b','a\nb','a\u0085b'])assert.throws(()=>validateChatInput({...input,model},conf()));
 for(const reasoningEffort of ['',null,'max',false])assert.throws(()=>validateChatInput({...input,reasoningEffort},conf()));
 assert.throws(()=>validateChatInput({...input,apiKey:'x'},conf()));
 assert.throws(()=>resolveChatSelection(conf({provider:'anthropic',model:'claude-3-7-sonnet',maxTokens:1024}),{reasoningEffort:'low'}),/1025/);
 assert.equal(validateConfig({},defaults).reasoningEffort,'auto');assert.throws(()=>validateConfig({model:'x\n'},defaults));
});
test('provider reasoning request shapes and budgets',()=>{
 for(const effort of ['auto','off','low','medium','high']){
  const b=requestBody(conf({model:'gpt-5-mini',reasoningEffort:effort}),user);
  assert.equal(b.max_completion_tokens,4096);assert.equal(b.max_tokens,undefined);
  assert.equal(b.reasoning_effort,effort==='auto'?undefined:effort==='off'?'none':effort);
  assert.equal('temperature' in b,false);
 }
 assert.equal(requestBody(conf(),user).temperature,0.2);
 assert.equal(requestBody(conf({model:'o3-mini'}),user).temperature,undefined);
 assert.equal(requestBody(conf({reasoningEffort:'high'}),user).max_tokens,4096);
 for(const model of ['claude-opus-4-6','claude-sonnet-4-6-20260217','claude-opus-4-7','claude-opus-4-8','claude-sonnet-5','claude-opus-5-5','claude-fable-5-1']){
  const b=requestBody(conf({provider:'anthropic',model,reasoningEffort:'high'}),user);
  assert.deepEqual(b.thinking,{type:'adaptive'});assert.deepEqual(b.output_config,{effort:'high'});assert.equal('temperature' in b,false);
 }
 for(const [effort,budget]of [['low',1024],['medium',4096],['high',8192]]){
  const b=requestBody(conf({provider:'anthropic',model:'claude-3-7-sonnet',reasoningEffort:effort,maxTokens:10000}),user);
  assert.deepEqual(b.thinking,{type:'enabled',budget_tokens:budget});assert.equal('temperature' in b,false);
 }
 assert.equal(requestBody(conf({provider:'anthropic',reasoningEffort:'high',maxTokens:2048}),user).thinking.budget_tokens,2047);
 assert.deepEqual(requestBody(conf({provider:'anthropic',reasoningEffort:'off'}),user).thinking,{type:'disabled'});
 assert.equal(requestBody(conf({provider:'anthropic',reasoningEffort:'auto'}),user).thinking,undefined);
 assert.equal(endpoint(conf({provider:'anthropic',baseUrl:'https://mock.test/v1/'}),'models'),'https://mock.test/v1/models');
});
test('Anthropic opaque blocks survive tool replay and never enter user events or persisted messages',async t=>{
 const original=globalThis.fetch,tokens=[{type:'thinking',thinking:'PRIVATE',signature:'OPAQUE'},{type:'redacted_thinking',data:'REDACTED'},{type:'tool_use',id:'tool1',name:'TAI_ListArtifacts',input:{}}],bodies=[];
 globalThis.fetch=async(url,options)=>{bodies.push(JSON.parse(options.body));return Response.json({content:bodies.length===1?tokens:[{type:'text',text:'Public answer'}]});};
 t.after(()=>globalThis.fetch=original);
 const store=new Store(await temp(t));await store.init();store.config=conf({provider:'anthropic',model:'claude-opus-4-6',reasoningEffort:'high'});
 const c=store.create(),events=[];
 await runChat({store,mcp:mcp(),conversation:c,message:'List',mode:'read',emit:e=>events.push(e),signal:new AbortController().signal,artifactDirectory:store.artifactDirectory(c)});
 assert.deepEqual(bodies[1].messages.find(m=>m.role==='assistant').content,tokens);
 assert.equal(bodies[1].messages.at(-1).content[0].type,'tool_result');
 assert.doesNotMatch(JSON.stringify([events,c]),/PRIVATE|OPAQUE|REDACTED|anthropicContent/);
});
test('explicit unsupported API reasoning errors propagate without retry and redact key',async t=>{
 const original=globalThis.fetch;let calls=0;globalThis.fetch=async()=>{calls++;return new Response('unsupported reasoning_effort key-secret',{status:400});};t.after(()=>globalThis.fetch=original);
 await assert.rejects(completion(conf({apiKey:'key-secret',reasoningEffort:'high'}),user),/unsupported reasoning_effort \[redacted\]/);assert.equal(calls,1);
});
test('model discovery caches, dedupes refresh, isolates config and paginates Anthropic',async()=>{
 let calls=0,time=0;const hold=gate();
 const discover=createModelDiscovery({now:()=>time,fetchImpl:async(url,options)=>{calls++;assert.equal(options.redirect,'error');if(calls===1)await hold.promise;return Response.json({data:[{id:'remote-'+calls,display_name:'Remote'}]});}});
 const config=conf(),a=discover(config),b=discover(config,{refresh:true});hold.release();assert.deepEqual(await a,await b);assert.equal(calls,1);
 await discover(config);assert.equal(calls,1);time=300001;await discover(config);assert.equal(calls,2);
 for(const patch of [{apiKey:'new'},{baseUrl:'https://other.test/v1'},{provider:'anthropic'},{model:'another'},{reasoningEffort:'high'}])await discover({...config,...patch});
 assert.equal(calls,7);
 const urls=[];const paged=createModelDiscovery({fetchImpl:async url=>{urls.push(url);return Response.json({data:[{id:'m'+urls.length}],has_more:urls.length===1,last_id:'cursor1'});}});
 const result=await paged(conf({model:'',provider:'anthropic'}));assert.deepEqual(result.models,[{id:'m1'},{id:'m2'}]);assert.match(urls[1],/after_id=cursor1/);assert.equal(result.source,'remote');
});
test('model discovery fallback redacts errors and times out using injected fetch',async()=>{
 const config=conf({apiKey:'secret-key'});
 const discover=createModelDiscovery({fetchImpl:async()=>new Response('api_key=secret-key Bearer other-secret',{status:401})});
 const result=await discover(config);assert.equal(result.source,'configured');assert.deepEqual(result.models,[{id:config.model}]);assert.doesNotMatch(JSON.stringify(result),/secret-key|other-secret/);
 const timed=createModelDiscovery({timeoutMs:5,fetchImpl:async(url,{signal})=>new Promise((resolve,reject)=>{const keepAlive=setTimeout(()=>reject(Error('timeout fixture failed')),1000);signal.addEventListener('abort',()=>{clearTimeout(keepAlive);reject(signal.reason);},{once:true});})});
 assert.match((await timed(config)).error,/timeout/i);
});
test('run snapshot freezes effective config before saving and rechecks tool permissions',async t=>{
 const store=new Store(await temp(t));await store.init();store.config=conf();const c=store.create(),entered=gate(),resume=gate(),seen=[],events=[];
 const save=store.save.bind(store);let once=true;store.save=async k=>{if(once){once=false;entered.release();await resume.promise;}return save(k);};
 let calls=0;const pending=runChat({store,mcp:mcp(),conversation:c,message:'x',mode:'artifact',model:'override',reasoningEffort:'high',emit:e=>events.push(e),signal:new AbortController().signal,artifactDirectory:store.artifactDirectory(c),modelCompletion:async(config,messages,tools)=>{seen.push({config,tools});return ++calls===1?{content:'',tool_calls:[{id:'forged',function:{name:'SaveProject',arguments:'{}'}}]}:{content:'Done'};}});
 await entered.promise;store.config.model='changed';store.config.apiKey='changed';resume.release();await pending;
 assert.equal(seen[0].config.model,'override');assert.equal(seen[1].config.apiKey,'');assert.ok(Object.isFrozen(seen[0].config));
 assert.equal(seen[0].tools.some(t=>t.name==='SaveProject'),false);assert.equal(events.find(e=>e.type==='tool_end').isError,true);
 assert.equal(c.messages[0].model,'override');assert.equal(c.messages[0].reasoningEffort,'high');assert.equal(store.config.reasoningEffort,'auto');
});
test('write lifecycle validates OpenProject bound path and verifies CloseProject',async t=>{
 const store=new Store(await temp(t));await store.init();store.config=conf();const c=store.create(),project=store.project('default');project.tiaProjectPath='C:/Bound/Project.ap19';const actual=[];
 const client={ready:true,connected:true,tools:['OpenProject','CloseProject','Connect','Disconnect'].map(name=>({name})),async call(name,args){actual.push({name,args});return {content:[{type:'text',text:JSON.stringify({IsConnected:true,ProjectPath:project.tiaProjectPath})}]};}};
 let step=0;const tool=(id,name,args)=>({id,function:{name,arguments:JSON.stringify(args)}});
 await runChat({store,mcp:client,conversation:c,project,message:'x',mode:'write',emit(){},signal:new AbortController().signal,artifactDirectory:store.artifactDirectory(c),modelCompletion:async()=>++step===1?{tool_calls:[tool('bad','OpenProject',{path:'C:/Other.ap19'}),tool('good','OpenProject',{path:'c:\\Bound\\Project.ap19'}),tool('close','CloseProject',{}),tool('connect','Connect',{})]}:{content:'Done'}});
 assert.deepEqual(actual.map(c=>c.name),['OpenProject','GetState','CloseProject','Connect']);
 assert.equal(c.messages[1].tools[0].isError,true);
});
test('artifact mode performs local write while read rejects forged write even after tool-list tampering',async t=>{
 const store=new Store(await temp(t));await store.init();store.config=conf();
 for(const mode of ['read','artifact']){
  const c=store.create(),events=[];let step=0;
  await runChat({store,mcp:mcp(),conversation:c,message:'create',mode,emit:e=>events.push(e),signal:new AbortController().signal,artifactDirectory:store.artifactDirectory(c),modelCompletion:async(config,messages,tools)=>{
   if(++step!==1)return {content:'Done'};
   if(mode==='read')tools.push({name:'TAI_WriteArtifact'});
   return {tool_calls:[{id:'write',function:{name:'TAI_WriteArtifact',arguments:JSON.stringify({name:'created.txt',content:'Local content'})}}]};
  }});
  assert.equal(events.find(e=>e.type==='tool_end').isError,mode==='read');
  if(mode==='artifact')assert.equal(await fs.readFile(path.join(store.artifactDirectory(c),'created.txt'),'utf8'),'Local content');
  else await assert.rejects(fs.stat(path.join(store.artifactDirectory(c),'created.txt')),e=>e.code==='ENOENT');
 }
});
test('discovery isolates concurrent credentials and prevents a stale response replacing current cache',async()=>{
 const held=gate(),entered=gate();const calls=[];
 const discover=createModelDiscovery({fetchImpl:async(url,{headers})=>{
  const key=headers.Authorization;calls.push(key);
  if(key==='Bearer credential-A'){entered.release();await held.promise;}
  return Response.json({data:[{id:key==='Bearer credential-A'?'old-model':'new-model'}]});
 }});
 const old=conf({apiKey:'credential-A'}),fresh=conf({apiKey:'credential-B'}),pending=discover(old);await entered.promise;
 assert.equal((await discover(fresh)).models[0].id,'new-model');held.release();assert.equal((await pending).models[0].id,'old-model');
 assert.equal((await discover(fresh)).models[0].id,'new-model');assert.deepEqual(calls,['Bearer credential-A','Bearer credential-B']);
});
test('HTTP validates selection before NDJSON or persistence; task/status/history expose effective selection',async t=>{
 const entered=gate(),resume=gate(),seen=[];
 const app=await createTaiServer({directory:await temp(t),mcpClient:mcp(),modelCompletion:async config=>{seen.push(config);entered.release();await resume.promise;return {content:'Done'};}});
 await new Promise(r=>app.server.listen(0,'127.0.0.1',r));t.after(()=>app.close());
 const base=`http://127.0.0.1:${app.server.address().port}`;
 const request=(route,data)=>fetch(base+route,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json'},body:data===undefined?undefined:JSON.stringify(data)});
 const c=await(await request('/api/conversations',{})).json(),input={conversationId:c.id,message:'x',mode:'artifact'};
 for(const patch of [{model:''},{reasoningEffort:'bogus'},{extra:true},{model:'a\nb'}]){const r=await request('/api/chat',{...input,...patch});assert.equal(r.status,400);assert.match(r.headers.get('content-type'),/application\/json/);}
 assert.deepEqual(app.store.get(c.id).messages,[]);
 app.store.config=conf({provider:'anthropic',model:'claude-3-7-sonnet',maxTokens:1024});
 assert.equal((await request('/api/chat',{...input,reasoningEffort:'low'})).status,400);assert.deepEqual(app.store.get(c.id).messages,[]);
 app.store.config=conf();
 const responsePromise=request('/api/chat',{...input,model:'override',reasoningEffort:'high'});await entered.promise;
 const status=await(await request('/api/status')).json();assert.equal(status.activeTask.model,'override');assert.equal(status.activeTask.mode,'artifact');assert.equal(status.activeTask.reasoningEffort,'high');
 resume.release();const response=await responsePromise,events=(await response.text()).trim().split('\n').map(JSON.parse);
 assert.equal(events[0].type,'task');assert.equal(events[0].model,'override');assert.equal(events[0].provider,'openai');assert.equal(app.store.config.model,'mock-model');assert.equal(seen[0].reasoningEffort,'high');
 const original=globalThis.fetch;globalThis.fetch=async(url,...args)=>String(url).startsWith(base)?original(url,...args):Response.json({data:[{id:'discovered'}]});t.after(()=>globalThis.fetch=original);
 assert.equal((await(await request('/api/models?refresh=1')).json()).models[0].id,'discovered');
 assert.equal((await request('/api/models?url=https://unwanted.test')).status,400);
});
