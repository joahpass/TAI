// Explicit live, read-only verification; not included in npm test.
import fs from 'node:fs/promises';
import { resultData } from '../server/results.mjs';
const base='http://127.0.0.1:17821';
const status=await (await fetch(base+'/api/status')).json();
if(status.busy || !status.mcp.connected) throw new Error('Requires idle, already-connected TAI; this script does not attach or modify projects.');
const results=[];
async function query(name,args={}) {
 const response=await fetch(base+'/api/tools/call',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name,arguments:args})});
 const envelope=await response.json(); const data=resultData(envelope);
 const attributes=[...(data?.Attributes??data?.attributes??[]),...(data?.Items??data?.items??[]).flatMap(item=>item.Attributes??item.attributes??[])];
 const failures=attributes.filter(a=>(a.Succeeded??a.succeeded)===false).map(a=>({Name:a.Name??a.name,ValueType:a.ValueType??a.valueType,Status:a.Status??a.status,Error:a.Error??a.error}));
 const summary={name,http:response.status,isError:Boolean(envelope.isError),meta:data?.Meta??data?.meta,items:(data?.Items??data?.items)?.length,failures,error:envelope.error??(!data?envelope.content:undefined)};
 results.push(summary); console.log(JSON.stringify(summary));
 return data;
}
await query('GetProject');
const devices=await query('GetDevices');
const device=(devices?.Items??devices?.items)?.[0];
if(device?.Name??device?.name) await query('GetDeviceInfo',{devicePath:device.Name??device.name});
if(process.env.TAI_TEST_DEVICE_ITEM) await query('GetDeviceItemInfo',{deviceItemPath:process.env.TAI_TEST_DEVICE_ITEM});
await fs.writeFile(new URL('./query-live-evidence.json',import.meta.url),JSON.stringify({at:new Date().toISOString(),version:status.version,results},null,2));
if(results.some(x=>x.http!==200 || x.isError || x.error || x.failures.length)) process.exitCode=1;
