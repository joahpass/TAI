import test from 'node:test';
import assert from 'node:assert/strict';
import { resolveMcpPath, defaultTiaConfig } from '../server/tia-version.mjs';
import { defaults, validateConfig } from '../server/store.mjs';

test('V19 and V21 route managed installed and development executables independently', () => {
  for (const tiaVersion of [19,21]) {
    assert.equal(resolveMcpPath({tiaVersion,mcpPath:'C:/Apps/TAI/mcp/TiaMcpServer.exe'},()=>true),`C:/Apps/TAI/mcp/v${tiaVersion}/TiaMcpServer.exe`);
    assert.equal(resolveMcpPath({tiaVersion,mcpPath:'E:/PLC SIM/mcp-v21/bin/V21/Release/net48/TiaMcpServer.exe'},()=>true),`E:/PLC SIM/mcp-v21/bin/V${tiaVersion}/Release/net48/TiaMcpServer.exe`);
  }
  assert.equal(resolveMcpPath({tiaVersion:19,mcpPath:'C:/custom/TiaMcpServer.exe'},()=>true),'C:/custom/TiaMcpServer.exe');
  assert.throws(()=>resolveMcpPath({tiaVersion:19,mcpPath:'C:/Apps/TAI/mcp/TiaMcpServer.exe'},()=>false),/V19/);
});
test('configuration accepts only supported SDK versions and matching directory names', () => {
  for (const tiaVersion of [19,21]) assert.equal(validateConfig({tiaVersion,tiaLocation:`C:/Siemens/Portal V${tiaVersion}`},defaults).tiaVersion,tiaVersion);
  for (const tiaVersion of [18,20,22,19.1,'19']) assert.throws(()=>validateConfig({tiaVersion},defaults),/19.*21/);
  assert.throws(()=>validateConfig({tiaVersion:19,tiaLocation:'E:/TIA Portal/Portal V21'},defaults),/不一致/);
  assert.equal(defaultTiaConfig({TAI_TIA_LOCATION:'E:/TIA Portal/Portal V21'}).tiaVersion,21);
  assert.equal(defaultTiaConfig({TAI_TIA_VERSION:'19'}).tiaVersion,19);
});
