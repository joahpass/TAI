// Explicit live check: exact existing PID, Connect/GetState/Disconnect only; no project writes.
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';
const [root,pidText,outFile]=process.argv.slice(2);
const pid=Number(pidText);
assert.ok(root&&Number.isInteger(pid)&&pid>0&&outFile,'Usage: node verify-connect.mjs ROOT PID REPORT');
const runtime=JSON.parse((await fs.readFile(path.join(root,'tia-runtime.json'),'utf8')).replace(/^\uFEFF/,''));
const {McpClient}=await import(pathToFileURL(path.join(root,'app/server/mcp.mjs')).href);
const client=new McpClient();
const evidence={at:new Date().toISOString(),root,tiaVersion:runtime.tiaVersion,processId:pid,operations:[],ok:false};
try {
 await client.start({...runtime,mcpPath:path.join(root,'mcp/TiaMcpServer.exe')});
 const connectTool=client.tools.find(t=>t.name==='Connect');
 assert.ok(connectTool.inputSchema.properties.processId,'Connect must support exact PID');
 for(const [name,args] of [['Connect',{processId:pid}],['GetState',{}],['Disconnect',{}]]) {
  const result=await client.call(name,args);
  evidence.operations.push({name,result});
  assert.notEqual(result.isError,true,`${name} returned error: ${JSON.stringify(result)}`);
  if(name==='GetState') assert.equal(client.connected,true,'Connected state expected');
 }
 evidence.ok=true;
 console.log('PASS: installed V21 Connect -> GetState -> Disconnect for PID '+pid);
} catch(error) {evidence.error=error.stack;console.error(error.message);process.exitCode=1;}
finally {
 if(client.connected)try {await client.call('Disconnect');}catch{}
 evidence.diagnostics=client.logs;
 client.stop();
 await fs.writeFile(outFile,JSON.stringify(evidence,null,2));
}
