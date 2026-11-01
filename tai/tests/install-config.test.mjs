import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { Store, defaults } from '../server/store.mjs';

test('installed MCP migration preserves custom paths and model secrets', async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'tai-install-config-'));
  const previous = process.env.TAI_MCP_PATH;
  process.env.TAI_MCP_PATH = 'C:/Apps/TAI/mcp/TiaMcpServer.exe';
  try {
    for (const legacy of ['E:/PLC SIM/mcp-v21/bin/V19/Release/net48/TiaMcpServer.exe', 'E:/PLC SIM/mcp-v21/bin/V21/Release/net48/TiaMcpServer.exe', 'E:/PLC SIM/mcp-v21/bin/Release/net48/TiaMcpServer.exe', 'E:\\PLC SIM\\mcp-v21\\bin\\Release\\net48\\TiaMcpServer.exe']) {
      await fs.writeFile(path.join(directory,'config.json'),JSON.stringify({...defaults,mcpPath:legacy,apiKey:'retained-secret'}));
      const store=new Store(directory); await store.init();
      assert.equal(store.config.mcpPath,process.env.TAI_MCP_PATH);
      assert.equal(store.config.apiKey,'retained-secret');
    }
    await fs.writeFile(path.join(directory,'config.json'),JSON.stringify({...defaults,mcpPath:'C:/custom/mcp.exe'}));
    const store=new Store(directory); await store.init();
    assert.equal(store.config.mcpPath,'C:/custom/mcp.exe');
  } finally {
    if(previous===undefined)delete process.env.TAI_MCP_PATH;else process.env.TAI_MCP_PATH=previous;
    await fs.rm(directory,{recursive:true,force:true});
  }
});
