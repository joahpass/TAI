import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { EngineeringExecution } from '../server/execution.mjs';
import { permitted } from '../server/agent.mjs';
const wrap=data=>({content:[{type:'text',text:JSON.stringify(data)}]});
test('block mutations compile only after verified edit and preserve save guard on failure',async()=>{
 const directory=await fs.mkdtemp(path.join(os.tmpdir(),'tai-edit-'));
 const calls=[];let editOk=true,compileOk=false;
 const call=async(name,args)=>{
  if(name==='GetState')return wrap({IsConnected:true,ProjectPath:'C:/A.ap21'});
  calls.push({name,args});
  if(['DeleteBlock','RenameBlock'].includes(name))return wrap({Succeeded:editOk,postconditionVerified:editOk,Meta:{success:editOk}});
  if(name==='CompileSoftware')return wrap({Succeeded:compileOk});
  return wrap({Meta:{success:true}});
 };
 const make=()=>new EngineeringExecution({directory,call,signal:new AbortController().signal,emit:()=>{}});
 try {
  for(const tool of ['DeleteBlock','RenameBlock']){assert.equal(permitted(tool,'read'),false);assert.equal(permitted(tool,'write'),true);}
  const deleted=await make().invoke('DeleteBlock',{softwarePath:'PLC',blockPath:'FB1',backupPath:'outside'});
  assert.equal(deleted.isError,true); // Deletion succeeded, but compile did not: no false all-clear.
  assert.deepEqual(calls.map(x=>x.name),['DeleteBlock','CompileSoftware']);
  assert.ok(calls[0].args.backupPath.startsWith(path.join(directory,'backups')));
  await assert.rejects(make().invoke('SaveProject',{}),/阻止保存/);
  compileOk=true;
  const renamed=await make().invoke('RenameBlock',{softwarePath:'PLC',blockPath:'FB2',newName:'FB_New'});
  assert.equal(renamed.isError,false);
  await make().invoke('SaveProject',{});
  editOk=false; const before=calls.filter(x=>x.name==='CompileSoftware').length;
  assert.equal((await make().invoke('DeleteBlock',{softwarePath:'PLC',blockPath:'missing'})).isError,true);
  assert.equal(calls.filter(x=>x.name==='CompileSoftware').length,before);
 } finally {await fs.rm(directory,{recursive:true,force:true});}
});
