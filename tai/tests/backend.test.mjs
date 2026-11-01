import test from 'node:test';
import assert from 'node:assert/strict';
import { defaults, validateConfig, publicConfig } from '../server/store.mjs';
import { permitted } from '../server/agent.mjs';
test('configuration preserves and redacts secrets', () => {
 const c = validateConfig({ apiKey: 'secret' }, defaults);
 assert.equal(publicConfig(c).apiKey, undefined);
 assert.equal(validateConfig({ apiKey: '' }, c).apiKey, 'secret');
 assert.equal(validateConfig({ clearApiKey: true }, c).apiKey, '');
 assert.equal(validateConfig({ maxSteps: 12 }, c).maxSteps, 0);
});
test('read mode blocks mutations and unknown tools', () => {
 assert.equal(permitted('GetProjectTree', 'read'), true);
 assert.equal(permitted('ImportBlock', 'read'), false);
 assert.equal(permitted('ImportBlock', 'write'), true);
 assert.equal(permitted('ImportBlocksFromDocuments', 'write'), false);
 assert.equal(permitted('GetHmiEditableObject', 'read'), true);
 for(const name of ['SetHmiObjectAttributes','ImportHmiObject','ExportHmiObject']) {
  assert.equal(permitted(name,'read'),false);
  assert.equal(permitted(name,'write'),true);
 }
 assert.equal(permitted('Download', 'write'), false);
});
