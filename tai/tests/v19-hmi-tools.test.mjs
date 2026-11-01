import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// Executes production V19Hmi*.cs tool bodies against an offline public-API fixture.
// No Siemens SDK, TIA process, project, or main-project restore/build is used.
test('V19 HMI real tool methods preserve read/edit/backup/readback contracts', { skip: process.platform !== 'win32' }, () => {
  const project = fileURLToPath(new URL('../../tests/v19-hmi/V19Hmi.Tests.csproj', import.meta.url));
  const run = spawnSync('dotnet', ['run', '--project', project], { stdio: 'inherit', timeout: 120000, windowsHide: true });
  assert.ifError(run.error);
  assert.equal(run.status, 0, 'offline HMI assertion runner must pass');
});
