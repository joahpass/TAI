import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {artifact} from '../server/agent.mjs';
test('large exported XML reads in bounded consistent Unicode pages', async () => {
 const root=await fs.mkdtemp(path.join(os.tmpdir(),'tai-large-artifact-'));
 try {
  const text='<Document>'+('变量😀'.repeat(260000))+'</Document>';
  await fs.writeFile(path.join(root,'Main.xml'),text);
  const first=await artifact(root,'TAI_ReadArtifact',{name:'Main.xml',maxChars:13});
  const info=JSON.parse(first.content[1].text);
  assert.ok(info.bytes>2_000_000); assert.equal(info.hasMore,true);
  assert.equal(first.content[0].text,text.slice(0,info.nextOffset));
  const second=await artifact(root,'TAI_ReadArtifact',{name:'Main.xml',offset:info.nextOffset,maxChars:13,expectedSha256:info.sha256});
  const next=JSON.parse(second.content[1].text);
  assert.equal(first.content[0].text+second.content[0].text,text.slice(0,next.nextOffset));
  const last=await artifact(root,'TAI_ReadArtifact',{name:'Main.xml',offset:text.length-11});
  assert.equal(JSON.parse(last.content[1].text).hasMore,false);
  assert.equal(last.content[0].text,text.slice(-11));
  await assert.rejects(artifact(root,'TAI_ReadArtifact',{name:'Main.xml',expectedSha256:'stale'}),/变化/);
  await assert.rejects(artifact(root,'TAI_ReadArtifact',{name:'Main.xml',offset:-1}),/offset/);
  await assert.rejects(artifact(root,'TAI_ReadArtifact',{name:'Main.xml',maxChars:20001}),/maxChars/);
 } finally {await fs.rm(root,{recursive:true,force:true});}
});
