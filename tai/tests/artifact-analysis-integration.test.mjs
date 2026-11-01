import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {artifact} from '../server/artifacts.mjs';
const data=r=>JSON.parse(r.content[0].text);
test('artifact inspection and search integrate hash checking with edits',async()=>{
 const dir=await fs.mkdtemp(path.join(os.tmpdir(),'tai-analysis-'));
 try{
  await artifact(dir,'TAI_WriteArtifact',{name:'plc/test.s7dcl',content:'DATA_BLOCK "Config"\nVAR\nTimer : Int;\nEND_VAR\nEND_DATA_BLOCK'});
  const info=data(await artifact(dir,'TAI_InspectArtifact',{name:'plc/test.s7dcl'}));
  assert.equal(info.importGuidance.route,'ImportFromDocuments');
  assert.equal(info.declarations[0].name,'Config');
  const found=data(await artifact(dir,'TAI_SearchArtifact',{name:'plc/test.s7dcl',query:'Timer',expectedSha256:info.sha256}));
  assert.equal(found.matches[0].line,3);
  await artifact(dir,'TAI_EditArtifact',{name:'plc/test.s7dcl',expectedSha256:info.sha256,oldText:'Timer : Int;',newText:'Timer : DInt;'});
  await assert.rejects(artifact(dir,'TAI_SearchArtifact',{name:'plc/test.s7dcl',query:'Timer',expectedSha256:info.sha256}),/SHA256/);
 }finally{await fs.rm(dir,{recursive:true,force:true});}
});
