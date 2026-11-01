import fs from 'node:fs/promises';
import path from 'node:path';
const conversations=JSON.parse(await fs.readFile(path.join(process.env.LOCALAPPDATA,'TAI','conversations.json'),'utf8'));
for(const c of conversations.slice(0,3)) for(const m of c.messages??[]) for(const t of m.tools??[]) {
 if(t.isError || t.result?.isError) console.log(JSON.stringify({conversation:c.id,date:m.createdAt,name:t.name,args:t.arguments,result:t.result}));
}
