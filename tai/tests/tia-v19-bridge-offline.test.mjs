import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { McpClient } from '../server/mcp.mjs';
import { resultData } from '../server/results.mjs';

const executable = v => fileURLToPath(new URL(`../../mcp-v21/bin/V${v}/Release/net48/TiaMcpServer.exe`,import.meta.url));
const originalTools = ['Connect','Disconnect','GetState','GetProject','OpenProject','SaveProject','SaveAsProject','CloseProject','GetProjectTree','GetDeviceInfo','GetDeviceItemInfo','GetDevices','GetSoftwareInfo','CompileSoftware','GetSoftwareTree','GetBlockInfo','GetBlocks','GetBlocksWithHierarchy','ExportBlock','ImportBlock','ExportBlocks','GetTypeInfo','GetTypes','ExportType','ImportType','ExportTypes','ExportAsDocuments','ExportBlocksAsDocuments','ImportFromDocuments','ImportBlocksFromDocuments','DeleteBlock','RenameBlock','BackupSoftwareForEdit','GetHmiSoftware','GetHmiScreens','GetHmiScreenContent','GetHmiTagTables','GetHmiConnections','GetHmiEditableObject','SetHmiObjectAttributes','ExportHmiObject','ImportHmiObject'];

test('V19 MCP exposes the complete original tool contract without a TIA installation', async () => {
  const mcp = new McpClient();
  try {
    await mcp.start({tiaVersion:19,tiaLocation:'C:/TAI-offline-missing-portal',mcpPath:executable(19)});
    const names=mcp.tools.map(t=>t.name);
    assert.equal(names.length,new Set(names).size,'no duplicate tool registration');
    for(const name of originalTools) assert.ok(names.includes(name),`missing original tool ${name}`);
    assert.ok(names.includes('ImportExternalSource'));
    const capabilities=resultData(await mcp.call('GetCompatibilityInfo'));
    assert.equal(capabilities.tiaVersion,19);
    assert.equal(capabilities.sdkLoaded,false);
    const state=resultData(await mcp.call('GetState'));
    assert.equal(state.IsConnected ?? state.isConnected,false);
    const failed=await mcp.call('GetProject');
    assert.equal(failed.isError,true);
    const sd=await mcp.call('ImportFromDocuments',{softwarePath:'fixture',groupPath:'',importPath:'C:/fixture',fileNameWithoutExtension:'A'});
    assert.equal(sd.isError,true,'V20+ SD capability must not pretend success');
    assert.equal(resultData(sd).requiredVersion,20);
  } finally { mcp.stop(); }
});

test('V19 input schemas preserve all V21 tool parameters and defaults (metadata only)', async t => {
  const location=process.env.TAI_TEST_V21_LOCATION || 'E:/TIA Portal/Portal V21';
  try { await fs.access(location+'/PublicAPI/V21/net48/Siemens.Engineering.Base.dll'); }
  catch { t.skip('Set TAI_TEST_V21_LOCATION to compare installed V21 API metadata'); return; }
  const old=new McpClient(), current=new McpClient();
  try {
    await old.start({tiaVersion:21,tiaLocation:location,mcpPath:executable(21)});
    await current.start({tiaVersion:19,tiaLocation:'C:/TAI-offline-missing-portal',mcpPath:executable(19)});
    const shape=s=>({required:[...(s.required??[])].sort(),properties:Object.fromEntries(Object.entries(s.properties??{}).sort(([a],[b])=>a.localeCompare(b)).map(([k,v])=>[k,{type:v.type,default:v.default}]))});
    assert.equal(old.tools.length,42);
    for(const tool of old.tools) {
      const port=current.tools.find(t=>t.name===tool.name);
      assert.ok(port,tool.name);
      assert.deepEqual(shape(port.inputSchema),shape(tool.inputSchema),tool.name);
    }
    const evidence={originalCount:old.tools.length,v19Count:current.tools.length,compared:old.tools.map(t=>t.name).sort(),liveProjectAccess:false};
    await fs.writeFile(new URL('./v19-contract-evidence.json',import.meta.url),JSON.stringify(evidence,null,2));
  } finally { old.stop(); current.stop(); }
});
