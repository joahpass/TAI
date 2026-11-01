// Checks the real packaged service in isolated data; never attaches to a TIA project.
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import net from 'node:net';
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import assert from 'node:assert/strict';
const root=path.resolve(fileURLToPath(new URL('..',import.meta.url)));
const json=async file=>JSON.parse((await fs.readFile(path.join(root,file),'utf8')).replace(/^\uFEFF/,''));
const preflight=Boolean(process.argv[2]);
const runtime=preflight ? {tiaVersion:Number(process.argv[2]),tiaLocation:process.argv[3]} : await json('tia-runtime.json');
const manifest=await json('version.json');
const sha=async file=>createHash('sha256').update(await fs.readFile(file)).digest('hex').toLowerCase();
assert.ok([19,21].includes(runtime.tiaVersion),'Invalid selected TIA version');
assert.ok(Number(process.versions.node.split('.')[0])>=22,'Bundled Node must be >=22');
const ownership=await json(preflight?'manifest.json':'.tai-install.json');
assert.equal(ownership.product,'TAI.UserLocal.1','Ownership metadata belongs to another product');
for(const entry of ownership.files) {
  const file=path.resolve(root,entry.path);
  assert.ok(file.startsWith(root+path.sep),'Manifest path escaped package');
  assert.equal(await sha(file),String(entry.sha256).toLowerCase(),`Hash mismatch: ${entry.path}`);
}
for(const file of ['TAI.Desktop.exe','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','app/frontend/dist/index.html','app/server/index.mjs','mcp/v19/TiaMcpServer.exe','mcp/v21/TiaMcpServer.exe']) {
  const info=await fs.stat(path.join(root,file));assert.ok(info.isFile()&&info.size>0,`Missing/empty package file: ${file}`);
}
// The AddIn is outside the ownership file list; verify exactly what the installer deployed.
if(preflight) {
  assert.deepEqual([...(ownership.addInVersions||[])].sort(),[21],'Payload must ship the V21 AddIn only');
  const addInHash=String(ownership.addInHashes?.['21']??'').toLowerCase();
  assert.ok(addInHash,'Missing AddIn hash');
  assert.equal(await sha(path.join(root,'addin/v21/TAI.addin')),addInHash,'AddIn payload hash mismatch');
} else if(ownership.addInHash) {
  assert.ok(ownership.addInRoot,'AddIn recorded without a root');
  const addIn=path.join(ownership.addInRoot,'TAI.addin');
  assert.equal(await sha(addIn),String(ownership.addInHash).toLowerCase(),'Installed AddIn hash mismatch');
  const bytes=await fs.readFile(addIn);
  // A TIA AddIn is a zip container; a truncated or placeholder file must never pass.
  assert.ok(bytes.length>0&&bytes[0]===0x50&&bytes[1]===0x4B,'AddIn is not a zip container');
  assert.ok(bytes.includes(Buffer.from('TAI.TIAADDIN')),'AddIn does not reference the TAI assembly');
}
const probe=net.createServer();
await new Promise((resolve,reject)=>{probe.once('error',reject);probe.listen(0,'127.0.0.1',resolve);});
const port=probe.address().port;
await new Promise(resolve=>probe.close(resolve));
const dataDir=await fs.mkdtemp(path.join(os.tmpdir(),'TAI-verify-'));
let child,output='',spawnError,exited;
const base=`http://127.0.0.1:${port}`;
async function get(endpoint,timeout=90000) {
  const response=await fetch(base+endpoint,{signal:AbortSignal.timeout(timeout)});
  assert.equal(response.status,200,`${endpoint}: ${await response.clone().text()}`);
  return response;
}
try {
  const env={...process.env,TAI_DATA_DIR:dataDir,TAI_PORT:String(port),TAI_MCP_PATH:path.join(root,'mcp/TiaMcpServer.exe')};
  delete env.TAI_OPEN;delete env.TAI_TIA_VERSION;delete env.TAI_TIA_LOCATION;
  // Preflight has no generated runtime file yet; the installed check deliberately reads it.
  if(preflight) {env.TAI_TIA_VERSION=String(runtime.tiaVersion);env.TAI_TIA_LOCATION=runtime.tiaLocation;}
  child=spawn(path.join(root,'runtime/node.exe'),[path.join(root,'app/server/index.mjs')],{cwd:root,windowsHide:true,env,stdio:['ignore','pipe','pipe']});
  exited=new Promise(resolve=>{child.once('exit',resolve);child.once('error',error=>{spawnError=error;resolve();});});
  child.stdout.on('data',b=>{output=(output+b).slice(-20000);});child.stderr.on('data',b=>{output=(output+b).slice(-20000);});
  let status;
  for(let i=0;i<150;i++) {
    if(spawnError)throw spawnError;
    if(child.exitCode!==null)throw new Error(`Packaged HTTP server exited: ${output}`);
    try {status=await (await get('/api/status',1000)).json();break;} catch {}
    await delay(100);
  }
  assert.equal(status?.app,'TAI',`Packaged HTTP service not ready: ${output}`);
  assert.equal(status.version,manifest.version,'Server/package version mismatch');
  const config=await (await get('/api/config')).json();
  assert.equal(config.tiaVersion,runtime.tiaVersion,'Installed version defaults');
  assert.equal(config.tiaLocation,runtime.tiaLocation,'Installed directory defaults');
  const html=await (await get('/')).text();
  assert.equal(html,await fs.readFile(path.join(root,'app/frontend/dist/index.html'),'utf8'));
  const assets=[...html.matchAll(/(?:src|href)="(\/assets\/[^"?#]+)"/g)].map(m=>m[1]);
  assert.ok(assets.length>0,'No frontend assets referenced');
  for(const asset of assets) {
    const response=await get(asset);
    assert.deepEqual(Buffer.from(await response.arrayBuffer()),await fs.readFile(path.join(root,'app/frontend/dist',asset)),'HTTP asset differs: '+asset);
  }
  console.log('Packaged HTTP service and frontend loaded; checking MCP initialization and tool schemas...');
  const {tools}=await (await get('/api/tools')).json();
  const names=new Set(tools.map(t=>t.name));
  for(const name of ['Connect','GetState','GetProject','CompileSoftware','ImportBlock','ImportHmiObject','SetHmiObjectAttributes']) assert.ok(names.has(name),`Missing tool ${name}`);
  assert.equal(names.size,runtime.tiaVersion===19?44:42,`V${runtime.tiaVersion} tool inventory`);
  assert.equal(tools.length,names.size,'Duplicate tool names');
  for(const tool of tools) {
    assert.equal(tool.inputSchema?.type,'object',`Invalid schema type: ${tool.name}`);
    assert.equal(tool.inputSchema.properties?.constructor,Object,`Missing properties: ${tool.name}`);
    assert.ok(typeof tool.description==='string'&&tool.description.length>0,`Missing description: ${tool.name}`);
  }
  const connect=tools.find(t=>t.name==='Connect');
  assert.ok(connect.inputSchema.properties.processId,'Connect must expose an exact processId');
  const call=async(name,args={})=>{
    const response=await fetch(base+'/api/tools/call',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name,arguments:args})});
    assert.equal(response.status,200,`${name}: ${await response.clone().text()}`);
    return response.json();
  };
  const state=await call('GetState');
  assert.notEqual(state.isError,true,'Offline GetState failed');
  const stateData=state.structuredContent??JSON.parse(state.content.find(c=>c.type==='text').text);
  const connected=stateData.IsConnected??stateData.isConnected;
  assert.equal(typeof connected,'boolean','GetState must report a boolean connection state');
  assert.equal(connected,false,'Verification must not leave a TIA connection open');
  const report={ok:true,phase:preflight?'preflight':'installed',version:manifest.version,tiaVersion:runtime.tiaVersion,tiaLocation:runtime.tiaLocation,node:process.versions.node,files:ownership.files.length,assets:assets.length,tools:names.size,addIn:Boolean(ownership.addInHash),connected,projectAttached:false,checkedUtc:new Date().toISOString()};
  if(!preflight)await fs.writeFile(path.join(root,'install-verification.json'),JSON.stringify(report,null,2));
  console.log('PASS: '+JSON.stringify(report));
} catch(error) {console.error(output);throw error;}
finally {
  // Stop the isolated service's MCP process tree; a 409 from a busy server must not leak it.
  if(child && !spawnError && child.exitCode===null) {
    try {await fetch(base+'/api/config',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({mcpPath:path.join(dataDir,'stopped.exe')}),signal:AbortSignal.timeout(5000)});}catch{}
    if(process.platform==='win32')spawnSync('taskkill',['/PID',String(child.pid),'/T','/F'],{stdio:'ignore'});
    else child.kill('SIGKILL');
    await Promise.race([exited,delay(5000)]);
  }
  for(let i=0;i<3;i++) { try {await fs.rm(dataDir,{recursive:true,force:true,maxRetries:3,retryDelay:200});break;} catch {await delay(300);} }
}
