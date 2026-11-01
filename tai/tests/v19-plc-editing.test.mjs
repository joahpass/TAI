import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// Runs the production Compatibility/V19PlcEditing.cs against an in-process fake public
// Siemens SDK. Never starts TIA, connects to a portal, or builds the main MCP project.
test('V19 PLC tools execute offline success and failure contracts', { timeout: 120_000 }, () => {
  const project = fileURLToPath(new URL('../../mcp-v21/tests/v19-plc/V19Plc.Tests.csproj', import.meta.url));
  const run = spawnSync('dotnet', ['run', '--project', project, '--configuration', 'Release'], {
    encoding: 'utf8', timeout: 110_000, windowsHide: true,
  });
  assert.ifError(run.error);
  assert.equal(run.status, 0, `${run.stdout}\n${run.stderr}`);
  assert.match(run.stdout, /V19 PLC offline tests passed: \d+/);
  assert.match(run.stdout, /PASS backup covers system technology units safety/);
  assert.match(run.stdout, /PASS external generation failure reports possible partial writes/);
});
