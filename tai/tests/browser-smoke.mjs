import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
const base=process.env.TAI_TEST_URL||'http://127.0.0.1:17821';
const profile=await fs.mkdtemp(path.join(os.tmpdir(),'tai-browser-'));
const browser=spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',['--headless=new','--disable-gpu','--no-first-run','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{stdio:'ignore'});
let ws;
try {
 let port;
 for(let i=0;i<150;i++){try{port=(await fs.readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0];break;}catch{await new Promise(r=>setTimeout(r,100));}}
 assert.ok(port,'Edge started');
 const target=await(await fetch(`http://127.0.0.1:${port}/json/new?${base}`,{method:'PUT'})).json();
 ws=new WebSocket(target.webSocketDebuggerUrl);
 await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject;});
 let id=0;const pending=new Map(),errors=[];
 ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);if(p){pending.delete(m.id);m.error?p.reject(m.error):p.resolve(m.result);}}else if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);};
 const call=(method,params={})=>new Promise((resolve,reject)=>{const key=++id;pending.set(key,{resolve,reject});ws.send(JSON.stringify({id:key,method,params}));});
 const evaluate=async expression=>(await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true})).result.value;
 const waitFor=async expression=>{for(let i=0;i<100;i++){if(await evaluate(expression))return;await new Promise(r=>setTimeout(r,100));}throw new Error(`UI condition failed: ${expression}\n${await evaluate('document.body.innerText')}`);};
 const screenshot=async filename=>{const shot=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await fs.writeFile(new URL('../'+filename,import.meta.url),Buffer.from(shot.data,'base64'));};
 await call('Runtime.enable');
 await call('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
 await waitFor('document.body.innerText.includes("诊断") && document.body.innerText.includes("模型")');
 assert.equal(await evaluate('Boolean(document.querySelector(".as-box"))'),false,'Console landing page has no daily chat composer');
 await waitFor('Boolean(document.querySelector("section[aria-labelledby=cs-prompt] textarea"))');
 if(process.env.TAI_TEST_PROMPTS==='1') {
   const initial=await evaluate('fetch("/api/prompts").then(r=>r.json())');
   await evaluate(`(()=>{const input=document.querySelector('input[aria-label="导入提示词文件"]');const dt=new DataTransfer();dt.items.add(new File([JSON.stringify({systemPrompt:'UI IMPORT TEST'})],'prompt.json',{type:'application/json'}));input.files=dt.files;input.dispatchEvent(new Event('change',{bubbles:true}));})()`);
   await waitFor('document.querySelector("section[aria-labelledby=cs-prompt] textarea").value==="UI IMPORT TEST"');
   assert.equal(await evaluate('fetch("/api/prompts").then(r=>r.json()).then(p=>p.systemPrompt)'),initial.systemPrompt,'Import stages draft');
   await evaluate('document.querySelector("section[aria-labelledby=cs-prompt] .btn-primary").click()');
   await waitFor('document.querySelector("section[aria-labelledby=cs-prompt]").innerText.includes("已保存，将在下一轮")');
   assert.equal(await evaluate('fetch("/api/prompts").then(r=>r.json()).then(p=>p.systemPrompt)'),'UI IMPORT TEST');
   await evaluate(`fetch('/api/prompts',{method:'PUT',headers:{'Content-Type':'application/json'},body:${JSON.stringify(JSON.stringify({systemPrompt:initial.systemPrompt}))}}).then(r=>r.json())`);
 }
 await screenshot('TAI-console-preview.png');
 await call('Emulation.setDeviceMetricsOverride',{width:520,height:860,deviceScaleFactor:1,mobile:false});
 await call('Page.navigate',{url:base+'/assistant'});
 await waitFor('document.title.includes("工程助手") && Boolean(document.querySelector(".wk-compact"))');
 assert.equal(await evaluate('Boolean(document.querySelector("input[type=password]"))'),false,'Assistant has no model secret form');
 assert.equal(await evaluate('document.documentElement.scrollWidth>window.innerWidth'),false,'Compact assistant has no horizontal overflow');
 assert.equal(errors.length,0,JSON.stringify(errors));
 assert.equal(await evaluate('Boolean(document.querySelector(".as-mode-desc"))'),false,'Removed mode disclaimer stays absent');
 if(await evaluate('Boolean(document.querySelector(".wk-compose-box"))')) {
   await evaluate('Array.from(document.querySelectorAll("button[role=radio]")).find(b=>b.textContent.trim()==="写入").click()');
   await waitFor('Boolean(document.querySelector(".as-box-write"))');
 }
 assert.equal(await evaluate('Boolean(document.querySelector(".as-mode-desc"))'),false,'Write mode also has no removed disclaimer');
 await screenshot('TAI-assistant-preview.png');
 await call('Emulation.setDeviceMetricsOverride',{width:364,height:480,deviceScaleFactor:1,mobile:false});
 assert.equal(await evaluate('document.documentElement.scrollWidth>window.innerWidth'),false,'Minimum-width assistant has no horizontal overflow');
 console.log('PASS: console and compact assistant routes, separate responsibilities, no runtime exceptions.');
 await call('Browser.close').catch(()=>{});
} finally {
 ws?.close();browser.kill();
 await new Promise(r=>setTimeout(r,500));
 await fs.rm(profile,{recursive:true,force:true}).catch(()=>{});
}
