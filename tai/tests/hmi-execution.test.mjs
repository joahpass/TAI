import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {EngineeringExecution} from '../server/execution.mjs';
const envelope=data=>({content:[{type:'text',text:JSON.stringify(data)}]});
test('HMI mutations require backup readback and compile before save',async()=>{
 const directory=await fs.mkdtemp(path.join(os.tmpdir(),'tai-hmi-'));
 let verified=false;const calls=[];
 const call=async(name,args)=>{calls.push({name,args});if(name==='GetState')return envelope({isConnected:true,projectPath:'C:/p.ap21'});if(name==='SetHmiObjectAttributes')return envelope({succeeded:true,meta:{success:true},postconditionVerified:verified,backupPath:args.backupPath,sha256:'a'.repeat(64)});if(name==='CompileSoftware')return envelope({succeeded:true,meta:{success:true}});return envelope({meta:{success:true}});};
 const make=()=>new EngineeringExecution({directory,call,signal:new AbortController().signal,emit:()=>{}});
 try{
  let exec=make();const args={softwarePath:'HMI/Panel',objectPath:'screens/Main',backupPath:'C:/untrusted.xml'};
  assert.equal((await exec.invoke('SetHmiObjectAttributes',args)).isError,true);
  assert.equal(calls.some(c=>c.name==='CompileSoftware'),false);
  await assert.rejects(make().invoke('SaveProject',{}),/未通过编译/);
  verified=true;assert.equal((await make().invoke('SetHmiObjectAttributes',args)).isError,false);
  const mutation=calls.find(c=>c.name==='SetHmiObjectAttributes');assert.ok(mutation.args.backupPath.startsWith(directory));assert.ok(mutation.args.backupPath.endsWith('object.xml'));
  await make().invoke('SaveProject',{});
 }finally{await fs.rm(directory,{recursive:true,force:true});}
});
