import assert from 'node:assert/strict';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
const installed=path.join(process.env.LOCALAPPDATA,'Programs/TAI/app/server/artifacts.mjs');
const {artifact}=await import(pathToFileURL(installed));
const root=path.join(process.env.LOCALAPPDATA,'TAI/artifacts');
const parse=r=>JSON.parse(r.content[0].text);
for(const name of ['DB_TrafficCfg.s7dcl','FC1_Control_v4.s7dcl']){
 const info=parse(await artifact(root,'TAI_InspectArtifact',{name}));
 assert.equal(info.importGuidance.route,'ImportFromDocuments');
 assert.ok(info.declarations.length);
 const search=parse(await artifact(root,'TAI_SearchArtifact',{name,query:info.declarations[0].name,expectedSha256:info.sha256}));
 assert.ok(search.matches.length);
 console.log(JSON.stringify({name,declarations:info.declarations,route:info.importGuidance.route,matches:search.matches.length}));
}
