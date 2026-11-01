import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { artifact } from '../server/agent.mjs';

test('read exported nested documents while confining paths and writes', async () => {
 const base = await fs.mkdtemp(path.join(os.tmpdir(), 'tai-artifact-'));
 const root = path.join(base, 'artifacts');
 try {
  await fs.mkdir(path.join(root,'led16','src'),{recursive:true});
  await fs.writeFile(path.join(root,'led16','src','Main.s7dcl'),'PROGRAM Main');
  for (const name of ['led16/src/Main.s7dcl','led16\\src\\Main.s7dcl']) {
   const result = await artifact(root,'TAI_ReadArtifact',{name});
   assert.equal(result.content[0].text,'PROGRAM Main');
  }
  for (const name of ['../outside.txt','led16/../../outside.txt','led16\\..\\..\\outside.txt','/outside.txt','C:\\outside.txt','//server/share/file.txt','led16//src/Main.s7dcl','led16/./src/Main.s7dcl','led16/src/Main.s7dcl:stream','led16/src/Main.exe']) {
   await assert.rejects(artifact(root,'TAI_ReadArtifact',{name}),undefined,name);
  }
  await artifact(root,'TAI_WriteArtifact',{name:'led16/src/new.scl',content:'test'});
  await fs.mkdir(path.join(root,'traffic_read','Main.xml'),{recursive:true});
  await fs.writeFile(path.join(root,'traffic_read','Main.xml','Main.xml'),'<Document/>');
  const directory = await artifact(root,'TAI_ReadArtifact',{name:'traffic_read/Main.xml'});
  const listing = JSON.parse(directory.content[0].text);
  assert.equal(listing.kind,'directory');
  assert.equal(listing.files[0].name,'traffic_read/Main.xml/Main.xml');
  const exportRoot = await artifact(root,'TAI_ReadArtifact',{name:'traffic_read'});
  assert.equal(JSON.parse(exportRoot.content[0].text).kind,'directory');
  await fs.writeFile(path.join(root,'binary.exe'),'not a supported artifact');
  await assert.rejects(artifact(root,'TAI_ReadArtifact',{name:'binary.exe'}),/仅支持/);
  const outside = path.join(base,'outside'); await fs.mkdir(outside);
  await fs.writeFile(path.join(outside,'secret.txt'),'private');
  await fs.symlink(outside,path.join(root,'linked'),'junction');
  await assert.rejects(artifact(root,'TAI_ReadArtifact',{name:'linked/secret.txt'}),/链接|联接/);
  await artifact(root,'TAI_WriteArtifact',{name:'new.scl',content:'code'});
  const read = await artifact(root,'TAI_ReadArtifact',{name:'new.scl'});
  const metadata = JSON.parse(read.content[1].text);
  await assert.rejects(artifact(root,'TAI_EditArtifact',{name:'new.scl',expectedSha256:'stale',oldText:'code',newText:'changed'}),/SHA256/);
  await assert.rejects(artifact(root,'TAI_EditArtifact',{name:'new.scl',expectedSha256:metadata.sha256,oldText:'missing',newText:'changed'}),/匹配/);
  const edited = await artifact(root,'TAI_EditArtifact',{name:'new.scl',expectedSha256:metadata.sha256,oldText:'code',newText:'changed'});
  const change = JSON.parse(edited.content[0].text);
  assert.equal(await fs.readFile(change.backupPath,'utf8'),'code');
  assert.equal(await fs.readFile(path.join(root,'new.scl'),'utf8'),'changed');
  assert.equal(change.imported,false);
  await assert.rejects(artifact(root,'TAI_EditArtifact',{name:'new.scl',expectedSha256:metadata.sha256,oldText:'changed',newText:'lost update'}),/SHA256/);
  await assert.rejects(artifact(root,'TAI_WriteArtifact',{name:'new.scl',content:'replace'}));
 } finally { await fs.rm(base,{recursive:true,force:true}); }
});
