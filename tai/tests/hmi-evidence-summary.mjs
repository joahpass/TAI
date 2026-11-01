import fs from 'node:fs/promises';
const {evidence}=JSON.parse(await fs.readFile(new URL('./hmi-live-evidence.json',import.meta.url),'utf8'));
for(const e of evidence){if(!e.name.startsWith('GetHmi'))continue;console.log(JSON.stringify({tool:e.name,records:e.data.records?.map(r=>({path:r.path,kind:r.kind})),xml:e.data.xml?{chars:e.data.totalChars,returned:e.data.returnedChars,nextOffset:e.data.nextOffset,complete:e.data.complete,screenName:e.data.screenName}:undefined}));}
