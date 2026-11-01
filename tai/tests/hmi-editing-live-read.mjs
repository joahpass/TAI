import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {resultData} from '../server/results.mjs';
const base='http://127.0.0.1:17821';
async function query(name,args){const r=await fetch(base+'/api/tools/call',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name,arguments:args})});const env=await r.json();assert.equal(env.isError,undefined,JSON.stringify(env));return resultData(env);}
const inventory=await query('GetHmiSoftware',{});const softwarePath=inventory.records[0].path;
const screens=await query('GetHmiScreens',{softwarePath});const objectPath=screens.records.find(x=>x.kind==='screen').path;
const properties=await query('GetHmiEditableObject',{softwarePath,objectPath});
assert.equal(properties.succeeded??properties.Succeeded,true,JSON.stringify(properties));
const backupPath=path.join(process.env.LOCALAPPDATA,'TAI/artifacts',`hmi-edit-baseline-${Date.now()}.xml`);
const exported=await query('ExportHmiObject',{softwarePath,objectPath,backupPath});
assert.equal(exported.succeeded??exported.Succeeded,true,JSON.stringify(exported));
assert.ok((await fs.stat(backupPath)).size>0);
console.log(JSON.stringify({softwarePath,objectPath,attributes:properties.attributes,exported},null,2));
