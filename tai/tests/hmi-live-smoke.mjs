// Explicit read-only verification against the user's already-connected project.
import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
import {resultData} from '../server/results.mjs';
const base='http://127.0.0.1:17821';
const status=await (await fetch(base+'/api/status')).json();
assert.equal(status.busy,false); assert.equal(status.mcp.connected,true);
const evidence=[];
async function query(name,args={}) {
 const r=await fetch(base+'/api/tools/call',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name,arguments:args})});
 const envelope=await r.json(), data=resultData(envelope);
 evidence.push({name,args,http:r.status,isError:!!envelope.isError,data});
 console.log(JSON.stringify({name,args,http:r.status,isError:!!envelope.isError,records:data?.records?.length,meta:data?.meta??data?.Meta,diagnostics:data?.diagnostics,error:envelope.error}));
 return data;
}
try {
 const tree=await query('GetProjectTree');
 const paths=tree?.paths??tree?.Paths??[];
 for(const p of paths.filter(p=>p.kind==='software')) await query('GetSoftwareInfo',{softwarePath:p.softwarePath??p.path});
 const hmi=await query('GetHmiSoftware');
 for(const software of hmi?.records??[]) {
  const softwarePath=software.path;
  const screens=await query('GetHmiScreens',{softwarePath});
  await query('GetHmiTagTables',{softwarePath});
  await query('GetHmiConnections',{softwarePath});
  if(process.env.TAI_TEST_HMI_ALIASES==='1' && screens.softwareType==='Siemens.Engineering.Hmi.HmiTarget') {
   for(const record of screens.records.filter(r=>r.kind==='screen'||r.path.startsWith('templates/')||r.path==='special/globalElements')) {
    if(record.kind==='folder'||record.kind.toLowerCase().includes('group')) continue;
    const page=await query('GetHmiScreenContent',{softwarePath,screenPath:record.path,maxChars:1000});
    assert.equal(page.screenPath,record.path);
    if(record.kind==='screen') {
     const alias=record.path.slice(record.path.indexOf('/')+1);
     const continuation=await query('GetHmiScreenContent',{softwarePath,screenPath:alias,maxChars:1000,offset:page.nextOffset??0,expectedSha256:page.sha256});
     assert.equal(continuation.screenPath,record.path);
     assert.equal(continuation.sha256,page.sha256);
    }
   }
  }
  const first=screens?.records?.find(r=>r.kind==='screen');
  if(first && screens.softwareType==='Siemens.Engineering.Hmi.HmiTarget') {
   let page=await query('GetHmiScreenContent',{softwarePath,screenPath:first.path,maxChars:20000});
   for(let i=0;page?.nextOffset!=null&&i<10;i++) page=await query('GetHmiScreenContent',{softwarePath,screenPath:first.path,maxChars:20000,offset:page.nextOffset,expectedSha256:page.sha256});
   assert.equal(page?.endOfDocument,true,'Screen XML reached final page');
  }
 }
 assert.ok((hmi?.records?.length??0)>0,'Current project must expose an HMI for live validation');
 assert.ok(evidence.every(e=>e.http===200&&!e.isError&&e.data),'Every read must succeed; inspect saved diagnostics');
} finally { await fs.writeFile(new URL('hmi-live-evidence.json',import.meta.url),JSON.stringify({at:new Date().toISOString(),version:status.version,evidence},null,2)); }
