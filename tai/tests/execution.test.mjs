import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { EngineeringExecution } from '../server/execution.mjs';
import { checkedResult } from '../server/results.mjs';
const currentProject=()=>result({IsConnected:true,ProjectPath:'C:/Projects/A/A.ap21'});
const result=data=>({content:[{type:'text',text:JSON.stringify(data)}]});

test('cancel after import keeps the pending compile guard after restart',async()=>{
 const directory=await fs.mkdtemp(path.join(os.tmpdir(),'tai-cancel-write-'));
 const controller=new AbortController();const calls=[];
 const call=async name=>{if(name==='GetState')return currentProject();calls.push(name);if(name==='BackupSoftwareForEdit')return result({Succeeded:true,complete:true,backupScope:'software-blocks',Meta:{success:true,totalBlocks:0,exportedBlocks:0}});controller.abort();return result({Meta:{success:true}});};
 try {
  const execution=new EngineeringExecution({directory,call,signal:controller.signal,emit:()=>{}});
  await assert.rejects(execution.invoke('ImportBlock',{softwarePath:'PLC'}),{name:'AbortError'});
  assert.deepEqual(calls,['BackupSoftwareForEdit','ImportBlock']);
  const next=new EngineeringExecution({directory,call,signal:new AbortController().signal,emit:()=>{}});
  await assert.rejects(next.invoke('SaveProject',{}),/阻止保存/);
 } finally {await fs.rm(directory,{recursive:true,force:true});}
});

test('project switches, RPC failures and path casing preserve the correct save guard',async()=>{
 const directory=await fs.mkdtemp(path.join(os.tmpdir(),'tai-project-guard-'));
 let project='C:/A.ap21',throws=true;
 const call=async name=>{
  if(name==='GetState')return result({IsConnected:true,ProjectPath:project});
  if(name==='CompileSoftware'&&throws)throw new Error('RPC timeout');
  return result({Succeeded:true,Meta:{success:true}});
 };
 const make=()=>new EngineeringExecution({directory,call,signal:new AbortController().signal,emit:()=>{}});
 try{
  await assert.rejects(make().invoke('CompileSoftware',{softwarePath:'PLC/Software'}),/RPC timeout/);
  await assert.rejects(make().invoke('SaveProject',{}),/阻止保存/);
  project='C:/B.ap21';throws=false;
  await make().invoke('CompileSoftware',{softwarePath:'PLC/Software'});
  await make().invoke('SaveProject',{});
  project='C:/A.ap21';
  await assert.rejects(make().invoke('SaveProject',{}),/阻止保存/);
  await make().invoke('CompileSoftware',{softwarePath:'plc/software'});
  await make().invoke('SaveProject',{});
  await assert.rejects(make().invoke('ImportBlocksFromDocuments',{softwarePath:'PLC'}),/逐个导入/);
 }finally{await fs.rm(directory,{recursive:true,force:true});}
});

test('compiler errors remain errors inside a successful MCP envelope',()=>{
 assert.equal(checkedResult(result({Succeeded:false,ErrorCount:2})).isError,true);
 assert.equal(checkedResult(result({Meta:{success:false}})).isError,true);
 assert.equal(Boolean(checkedResult(result({Succeeded:true,ErrorCount:0,WarningCount:2})).isError),false);
});

test('engineering import backs up first, compiles after and blocks save across turns on errors',async()=>{
 const dir=await fs.mkdtemp(path.join(os.tmpdir(),'tai-execution-'));
 const calls=[],events=[];let compileOk=false,backupOk=true;
 const call=async(name,args)=>{
   if(name==='GetState')return currentProject();
  calls.push(name);
  if(name==='BackupSoftwareForEdit') return result({Succeeded:backupOk,complete:backupOk,backupScope:'software-blocks',Meta:{success:backupOk,totalBlocks:2,exportedBlocks:backupOk?2:1,inconsistentBlocks:0}});
  if(name==='CompileSoftware') return result({Succeeded:compileOk,ErrorCount:compileOk?0:1,Messages:[{Description:'Compile diagnostic'}]});
  return result({Meta:{success:true}});
 };
 const make=()=>new EngineeringExecution({directory:dir,call,signal:new AbortController().signal,emit:e=>events.push(e)});
 try {
  const imported=await make().invoke('ImportBlock',{softwarePath:'PLC/Software',importPath:'code.xml'});
  assert.equal(imported.isError,true);
  assert.deepEqual(calls,['BackupSoftwareForEdit','ImportBlock','CompileSoftware']);
  assert.ok(events.some(e=>e.name==='CompileSoftware'&&e.type==='tool_end'&&e.isError));
  await assert.rejects(make().invoke('SaveProject',{}),/阻止保存/);
  compileOk=true;
  await make().invoke('CompileSoftware',{softwarePath:'PLC/Software'});
  await make().invoke('SaveProject',{});
  assert.equal(calls.at(-1),'SaveProject');
  backupOk=false;const before=calls.filter(x=>x==='ImportBlock').length;
  await assert.rejects(make().invoke('ImportBlock',{softwarePath:'PLC/Software',importPath:'code.xml'}),/备份未完整/);
  assert.equal(calls.filter(x=>x==='ImportBlock').length,before);
 } finally {await fs.rm(dir,{recursive:true,force:true});}
});

test('production camelCase envelopes permit verified backup and compile flow',async()=>{
 const directory=await fs.mkdtemp(path.join(os.tmpdir(),'tai-camel-'));
 const calls=[];
 const call=async name=>{
  calls.push(name);
  if(name==='GetState')return result({isConnected:true,projectPath:'C:/A.ap21'});
  if(name==='BackupSoftwareForEdit')return result({succeeded:true,complete:true,backupScope:'software-blocks',meta:{success:true}});
  if(name==='CompileSoftware')return result({succeeded:true,errorCount:0});
  return result({meta:{success:true}});
 };
 try{
  const execution=new EngineeringExecution({directory,call,signal:new AbortController().signal,emit:()=>{}});
  assert.equal((await execution.invoke('ImportBlock',{softwarePath:'PLC',importPath:'block.xml'})).isError,false);
  await execution.invoke('SaveProject',{});
  assert.ok(calls.includes('CompileSoftware'));
 }finally{await fs.rm(directory,{recursive:true,force:true});}
});

test('legacy compile response without positive verification cannot clear pending change',async()=>{
 const dir=await fs.mkdtemp(path.join(os.tmpdir(),'tai-compile-'));
 try {
  const execution=new EngineeringExecution({directory:dir,call:async name=>name==='GetState'?currentProject():result({Message:'Compilation finished'}),signal:new AbortController().signal,emit:()=>{}});
  assert.equal((await execution.invoke('CompileSoftware',{softwarePath:'PLC'})).isError,true);
  await assert.rejects(execution.invoke('SaveProject',{}),/阻止保存/);
 } finally {await fs.rm(dir,{recursive:true,force:true});}
});
