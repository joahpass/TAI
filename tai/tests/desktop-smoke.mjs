import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
const mode=process.env.TAI_TEST_MODE||'console';
async function profilePort(profile){return (await fs.readFile(process.env.LOCALAPPDATA+'/TAI/'+profile+'/EBWebView/DevToolsActivePort','utf8')).split('\n')[0].trim();}
const port=process.env.TAI_CDP_PROFILE?await profilePort(process.env.TAI_CDP_PROFILE):process.env.TAI_CDP_PORT||17822;
const targets=await(await fetch('http://127.0.0.1:'+port+'/json')).json();
const page=targets.find(t=>t.type==='page'&&t.url.startsWith('http://127.0.0.1:17821')&&(new URL(t.url).pathname.startsWith('/assistant')===(mode==='assistant')));
assert.ok(page,'Expected WebView route '+mode);
const ws=new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject;});
let id=0;const pending=new Map();
ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);if(p){pending.delete(m.id);m.error?p.reject(m.error):p.resolve(m.result);}}};
const call=(method,params={})=>new Promise((resolve,reject)=>{const key=++id;pending.set(key,{resolve,reject});ws.send(JSON.stringify({id:key,method,params}));});
try{
 const evaluate=async expression=>(await call('Runtime.evaluate',{expression,returnByValue:true})).result.value;
 const text=await evaluate('document.body.innerText');
 if(mode==='assistant'){
  assert.ok((await evaluate('document.title')).includes('工程助手'));
  assert.ok(await evaluate('Boolean(document.querySelector("textarea"))'));
  assert.equal(await evaluate('Boolean(document.querySelector("input[type=password]"))'),false);
 }else{
  assert.ok(text.includes('诊断')&&text.includes('模型'));
  assert.equal(await evaluate('Boolean(document.querySelector("textarea"))'),false);
 }
 const status=await(await fetch('http://127.0.0.1:17821/api/status')).json();
 assert.equal(status.app,'TAI');assert.equal(status.version,process.env.TAI_TEST_VERSION||'0.3.0');
 if(process.env.TAI_TEST_LIVE!=='1') assert.equal(status.mcp.running,false,'Isolated smoke starts without MCP');
 const image=await call('Page.captureScreenshot',{format:'png'});
 await fs.writeFile(new URL('../TAI-installed-'+mode+'.png',import.meta.url),Buffer.from(image.data,'base64'));
 if(mode==='assistant'&&process.env.TAI_TEST_OPEN_CONSOLE==='1'){
  await call('Page.bringToFront');
  const point=await evaluate("(()=>{const a=[...document.querySelectorAll('a[target=_blank]')].find(a=>new URL(a.href).pathname==='/');if(!a)return null;const r=a.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()");
  assert.ok(point,'Separate console link');
  await call('Input.dispatchMouseEvent',{type:'mousePressed',button:'left',clickCount:1,...point});
  await call('Input.dispatchMouseEvent',{type:'mouseReleased',button:'left',clickCount:1,...point});
  let opened=false;
  for(let i=0;i<100;i++){
   try{
    const cp=process.env.TAI_CDP_PROFILE?await profilePort('WebView2'):port;
    const pages=await(await fetch('http://127.0.0.1:'+cp+'/json')).json();
    if(pages.some(t=>t.type==='page'&&t.url==='http://127.0.0.1:17821/')){opened=true;break;}
   }catch{}
   await new Promise(r=>setTimeout(r,100));
  }
  assert.ok(opened,'User click opens separate console WebView');
  assert.ok((await evaluate('location.pathname')).startsWith('/assistant'));
 }
 console.log('PASS: '+mode+' route, separated responsibilities and v'+status.version+' service; no engineering calls issued by test.');
}finally{ws.close();}
