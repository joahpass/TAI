import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {artifact} from '../server/artifacts.mjs';
const data=r=>JSON.parse(r.content[0].text);
test('nested batch edits, encoding and large files',async()=>{
 const root=await fs.mkdtemp(path.join(os.tmpdir(),'tai-edit-'));
 const call=(name,args)=>artifact(root,'TAI_'+name,args);
 try {
  await call('WriteArtifact',{name:'new/docs/a.md',content:'one two one'});
  const meta=JSON.parse((await call('ReadArtifact',{name:'new/docs/a.md'})).content[1].text);
  const args={name:'new/docs/a.md',expectedSha256:meta.sha256};
  await assert.rejects(call('EditArtifact',{...args,edits:[{oldText:'two',newText:'three'},{oldText:'missing',newText:'x'}]}));
  assert.equal(await fs.readFile(path.join(root,args.name),'utf8'),'one two one');
  const change=data(await call('EditArtifact',{...args,edits:[{oldText:'one',newText:'1',replaceAll:true},{oldText:'two',newText:'2'}]}));
  assert.equal(await fs.readFile(path.join(root,args.name),'utf8'),'1 2 1');
  assert.equal(await fs.readFile(change.backupPath,'utf8'),'one two one');
  await assert.rejects(call('EditArtifact',{...args,content:'stale'}));
  await call('EditArtifact',{name:args.name,expectedSha256:change.sha256,content:'replacement'});
  for(const encoding of ['le','be']){
   const file='encoded-'+encoding+'.xml';let body=Buffer.from('<Text>中文😀</Text>','utf16le');if(encoding==='be')body=body.swap16();
   await fs.writeFile(path.join(root,file),Buffer.concat([Buffer.from(encoding==='le'?[255,254]:[254,255]),body]));
   const read=await call('ReadArtifact',{name:file});assert.equal(read.content[0].text,'<Text>中文😀</Text>');
   const m=JSON.parse(read.content[1].text);
   await call('EditArtifact',{name:file,expectedSha256:m.sha256,oldText:'中文',newText:'工程'});
   const bytes=await fs.readFile(path.join(root,file));assert.equal(bytes[0],encoding==='le'?255:254);
   assert.equal((await call('ReadArtifact',{name:file})).content[0].text,'<Text>工程😀</Text>');
  }
  const first=data(await call('ListArtifacts',{limit:1}));assert.equal(first.files.length,1);assert.equal(first.hasMore,true);
  const second=data(await call('ListArtifacts',{offset:first.nextOffset,limit:1}));assert.notEqual(second.files[0].name,first.files[0].name);
  for(const name of ['CON.txt','a/NUL.md','../outside.md'])await assert.rejects(call('WriteArtifact',{name,content:'x'}));
  await fs.writeFile(path.join(root,'bad.txt'),Buffer.from([255,255,0]));await assert.rejects(call('ReadArtifact',{name:'bad.txt'}));
  const big='a'.repeat(2_100_000)+'END';await call('WriteArtifact',{name:'big.st',content:big});
  const bigMeta=JSON.parse((await call('ReadArtifact',{name:'big.st'})).content[1].text);
  await call('EditArtifact',{name:'big.st',expectedSha256:bigMeta.sha256,oldText:'END',newText:'DONE'});
  assert.equal((await fs.readFile(path.join(root,'big.st'),'utf8')).endsWith('DONE'),true);
 } finally {await fs.rm(root,{recursive:true,force:true});}
});
