import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { completion } from '../server/model.mjs';
import { defaults, Store } from '../server/store.mjs';
import { runChat, artifact } from '../server/agent.mjs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
test('agent tool loop persists results and rejects artifact traversal', async () => {
 const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'tai-loop-'));
 let count = 0, calls = 0;
 const server = http.createServer(async (req, res) => {
  for await (const chunk of req) { /* drain request */ }
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({choices:[{message: ++count === 1 ? {content:'',tool_calls:[{id:'a',type:'function',function:{name:'GetState',arguments:'{}'}}]} : {content:'工程状态已读取'}}]}));
 });
 await new Promise(r => server.listen(0, '127.0.0.1', r));
 try {
  const store = new Store(dir); await store.init();
  store.config = {...defaults, model:'mock',baseUrl:`http://127.0.0.1:${server.address().port}`};
  const conversation = store.create(), events = [];
  const mcp = {ready:true,connected:true,tools:[{name:'GetState',inputSchema:{type:'object'}}],call:async()=>{calls++;return {content:[{type:'text',text:'{}'}]};}};
  await runChat({store,mcp,conversation,message:'检查工程',mode:'read',emit:e=>events.push(e),signal:new AbortController().signal,artifactDirectory:dir});
  assert.equal(calls,1);assert.equal(events.at(-1).type,'done');
  const loaded = new Store(dir);await loaded.init();assert.equal(loaded.get(conversation.id).messages[1].tools.length,1);
  await assert.rejects(artifact(dir,'TAI_ReadArtifact',{name:'../outside.xml'}));
  await artifact(dir,'TAI_WriteArtifact',{name:'block.xml',content:'<test/>'});
  await assert.rejects(artifact(dir,'TAI_WriteArtifact',{name:'block.xml',content:'overwrite'}));
 } finally {
  await new Promise(r=>server.close(r));await fs.rm(dir,{recursive:true,force:true});
 }
});
test('unlimited loop exceeds former cap and custom prompt reaches provider',async()=>{
 const dir=await fs.mkdtemp(path.join(os.tmpdir(),'tai-unlimited-'));let count=0,seen;
 const server=http.createServer(async(req,res)=>{let raw='';for await(const c of req)raw+=c;seen=JSON.parse(raw);count++;res.setHeader('Content-Type','application/json');res.end(JSON.stringify({choices:[{message:count<=35?{content:'',tool_calls:[{id:String(count),type:'function',function:{name:'GetState',arguments:'{}'}}]}:{content:'finished'}}]}));});
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 try{
 const store=new Store(dir);await store.init();store.config={...defaults,maxSteps:1,systemPrompt:'CUSTOM ENGINEERING PROMPT',model:'mock',baseUrl:`http://127.0.0.1:${server.address().port}`};
 const events=[];await runChat({store,conversation:store.create(),mcp:{ready:true,connected:true,tools:[{name:'GetState',inputSchema:{type:'object'}}],call:async()=>({content:[]})},message:'go',mode:'read',emit:e=>events.push(e),signal:new AbortController().signal,artifactDirectory:dir});
 assert.equal(count,36);assert.equal(events.filter(e=>e.type==='tool_end').length,35);assert.equal(seen.messages[0].content,'CUSTOM ENGINEERING PROMPT');assert.equal(events.some(e=>e.type==='error'),false);
 }finally{await new Promise(r=>server.close(r));await fs.rm(dir,{recursive:true,force:true});}
});
test('provider adapters send and parse real HTTP', async () => {
 const received=[];
 const server=http.createServer(async(req,res)=>{
  let raw=''; for await(const chunk of req)raw+=chunk;
  received.push(JSON.parse(raw));
  res.setHeader('Content-Type','application/json');
  res.end(JSON.stringify(req.url.endsWith('/messages') ? {content:[{type:'text',text:'anthropic ok'}]} : {choices:[{message:{content:'openai ok'}}]}));
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 try {
  const config={...defaults,model:'mock',baseUrl:`http://127.0.0.1:${server.address().port}`};
  const messages=[{role:'system',content:'test'},{role:'user',content:'hello'}];
  assert.equal((await completion(config,messages)).content,'openai ok');
  assert.equal((await completion({...config,provider:'anthropic'},messages)).content,'anthropic ok');
  assert.equal(received[1].system,'test');
 } finally {await new Promise(resolve=>server.close(resolve));}
});
