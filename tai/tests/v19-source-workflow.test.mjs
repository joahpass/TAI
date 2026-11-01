import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { EngineeringExecution } from '../server/execution.mjs';
import { permitted, effect } from '../server/agent.mjs';

const response = data => ({content:[{type:'text',text:JSON.stringify(data)}]});
async function fixture(typeComplete=true,compilePass=true) {
  const directory=await fs.mkdtemp(path.join(os.tmpdir(),'tai-source-workflow-'));
  const calls=[];
  const execution=new EngineeringExecution({directory,signal:new AbortController().signal,emit:()=>{},call:async(name,args)=>{
    calls.push({name,args});
    if(name==='GetState')return response({IsConnected:true,ProjectPath:'C:/fixture.ap19'});
    if(name==='ExportTypes')return response({Meta:{success:typeComplete,totalTypes:2,exportedTypes:typeComplete?2:1,inconsistentTypes:0}});
    if(name==='BackupSoftwareForEdit')return response({Succeeded:true,complete:true,backupScope:'software-blocks',Meta:{success:true}});
    if(name==='CompileSoftware')return response({Succeeded:compilePass,ErrorCount:compilePass?0:1,Meta:{success:compilePass}});
    return response({Succeeded:true,Meta:{success:true}});
  }});
  return {directory,calls,execution};
}
test('external source generation requires write mode and complete type plus block backups',async()=>{
  assert.equal(effect('ImportExternalSource'),'write');
  assert.equal(permitted('ImportExternalSource','read'),false);
  assert.equal(permitted('ImportExternalSource','write'),true);
  const {execution,calls}=await fixture();
  const result=await execution.invoke('ImportExternalSource',{softwarePath:'Device/CPU',importPath:'C:/fixture.scl'});
  assert.equal(result.isError,false);
  const operations=calls.map(c=>c.name).filter(n=>n!=='GetState');
  assert.deepEqual(operations,['ExportTypes','BackupSoftwareForEdit','ImportExternalSource','CompileSoftware']);
  await execution.invoke('SaveProject',{});
  assert.equal(calls.at(-1).name,'SaveProject');
});
test('incomplete type backup blocks source mutation; failed source compile blocks save',async()=>{
  const partial=await fixture(false);
  await assert.rejects(partial.execution.invoke('ImportExternalSource',{softwarePath:'Device/CPU',importPath:'C:/fixture.scl'}),/备份/);
  assert.ok(!partial.calls.some(c=>c.name==='ImportExternalSource'));
  const failed=await fixture(true,false);
  const result=await failed.execution.invoke('ImportExternalSource',{softwarePath:'Device/CPU',importPath:'C:/fixture.scl'});
  assert.equal(result.isError,true);
  await assert.rejects(failed.execution.invoke('SaveProject',{}),/阻止保存/);
  assert.ok(!failed.calls.some(c=>c.name==='SaveProject'));
});
