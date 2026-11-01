import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { inspectText, searchText } from './artifact-analysis.mjs';
const MAX=64_000_000;
const extensions=/\.(scl|xml|s7dcl|s7res|txt|json|md|csv|yaml|yml|ini|st|udt|awl|js|ts|css|html|log)$/iu;
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const result=data=>({content:[{type:'text',text:JSON.stringify(data)}]});
const editSchema={type:'object',properties:{oldText:{type:'string'},newText:{type:'string'},replaceAll:{type:'boolean'}},required:['oldText','newText'],additionalProperties:false};
export const artifactTools=[
 {name:'TAI_WriteArtifact',description:'创建工件目录内文本文件，自动创建子目录；不覆盖已有文件，不自动导入工程。支持 PLC 文本、XML、JSON、Markdown、CSV 和常见代码文件，最大64MB。',inputSchema:{type:'object',properties:{name:{type:'string'},content:{type:'string'}},required:['name','content'],additionalProperties:false}},
 {name:'TAI_ReadArtifact',description:'读取相对路径文本，最大64MB。offset为字符偏移，maxChars默认20000；继续按nextOffset读取，并传expectedSha256。返回真实编码及SHA256；目录返回分页文件清单。',inputSchema:{type:'object',properties:{name:{type:'string'},offset:{type:'integer',minimum:0},maxChars:{type:'integer',minimum:1,maximum:20000},expectedSha256:{type:'string'}},required:['name'],additionalProperties:false}},
 {name:'TAI_EditArtifact',description:'修改已读取工件，必须提供expectedSha256。三选一：oldText/newText精确替换；edits数组顺序批量替换；content整文件更新。默认旧文本须唯一；replaceAll=true明确替换全部匹配。批次全部匹配后一次写入，备份原文件，保留UTF8/UTF16编码，不自动导入。',inputSchema:{type:'object',properties:{name:{type:'string'},expectedSha256:{type:'string'},oldText:{type:'string'},newText:{type:'string'},replaceAll:{type:'boolean'},edits:{type:'array',items:editSchema,minItems:1,maxItems:1000},content:{type:'string'}},required:['name','expectedSha256'],additionalProperties:false}},
 {name:'TAI_ListArtifacts',description:'分页列出工件目录；path为空表示根目录。返回子文件准确相对路径，按nextOffset继续列出。',inputSchema:{type:'object',properties:{path:{type:'string'},offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:100}},additionalProperties:false}}
 ];
artifactTools.push(
 {name:'TAI_InspectArtifact',description:'分析工程文本的格式、声明块、语言、引用和 XML 对象概要，给出适用导入接口。仅词法分析，不是语法/编译验证；返回 SHA256，扫描或结果截断会明确标示。',inputSchema:{type:'object',properties:{name:{type:'string'}},required:['name'],additionalProperties:false}},
 {name:'TAI_SearchArtifact',description:'在整个工件中做字面文本搜索（非正则），返回字符偏移、行列和上下文。用于定位修改对象；按 nextOffset 继续搜索，分页传 expectedSha256。',inputSchema:{type:'object',properties:{name:{type:'string'},query:{type:'string'},offset:{type:'integer',minimum:0},limit:{type:'integer',minimum:1,maximum:100},contextLines:{type:'integer',minimum:0,maximum:5},caseSensitive:{type:'boolean'},expectedSha256:{type:'string'}},required:['name','query'],additionalProperties:false}}
);
function decode(bytes) {
 let encoding='utf-8',bom=Buffer.alloc(0),body=bytes;
 if(bytes[0]===0xff&&bytes[1]===0xfe){encoding='utf-16le';bom=bytes.subarray(0,2);body=bytes.subarray(2);}
 else if(bytes[0]===0xfe&&bytes[1]===0xff){encoding='utf-16be';bom=bytes.subarray(0,2);body=bytes.subarray(2);}
 else if(bytes.subarray(0,3).equals(Buffer.from([0xef,0xbb,0xbf]))){bom=bytes.subarray(0,3);body=bytes.subarray(3);}
 let text;try{text=new TextDecoder(encoding,{fatal:true,ignoreBOM:true}).decode(body);}catch{throw new Error('文件编码无效；仅支持 UTF-8 和带 BOM 的 UTF-16 文本');}
 if(text.includes('\0'))throw new Error('文件包含二进制 NUL 字符，不能作为文本编辑');
 return {text,encoding,bom};
}
function encode(text,{encoding,bom}) {
 if(typeof text!=='string'||text.includes('\0')||!text.isWellFormed())throw new Error('内容必须是有效的 Unicode 文本');
 const bytes=Buffer.from(text,encoding==='utf-8'?'utf8':'utf16le');
 if(encoding==='utf-16be')bytes.swap16();
 const output=Buffer.concat([bom,bytes]);
 if(output.length>MAX)throw new Error('工件超过 64 MB 上限');
 return output;
}
async function resolve(root,relative,createParents=false,allowRoot=false) {
 if(typeof relative!=='string')throw new Error('必须提供工件相对路径');
 relative=relative.replaceAll('\\','/');
 if(allowRoot&&(relative===''||relative==='.'))return {target:root,relative:''};
 const parts=relative.split('/');
 if(!relative||path.posix.isAbsolute(relative)||path.win32.isAbsolute(relative)||parts.some(p=>! /^[\p{L}\p{N}_ .-]+$/u.test(p)||p==='.'||p==='..'||/[. ]$/.test(p)||/^(con|prn|aux|nul|com[0-9¹²³]|lpt[0-9¹²³])(?:\.|$)/i.test(p)))throw new Error('工件路径必须位于工件目录内，禁止绝对路径、上级目录或保留名称');
 let target=root;
 for(let i=0;i<parts.length;i++){
  target=path.join(target,parts[i]);
  let stat;try{stat=await fs.lstat(target);}catch(e){if(e.code!=='ENOENT')throw e;}
  if(!stat&&createParents&&i<parts.length-1){await fs.mkdir(target);stat=await fs.lstat(target);}
  if(stat?.isSymbolicLink())throw new Error('工件路径不能包含符号链接或目录联接');
  if(stat&&i<parts.length-1&&!stat.isDirectory())throw new Error('工件父路径不是目录');
 }
 return {target,relative};
}
async function listing(target,relative,args){
 const offset=args.offset??0,limit=args.limit??100;
 if(!Number.isSafeInteger(offset)||offset<0||!Number.isSafeInteger(limit)||limit<1||limit>100)throw new Error('目录 offset 必须非负，limit 必须为1–100');
 const entries=(await fs.readdir(target,{withFileTypes:true})).filter(e=>!e.isSymbolicLink()&&(e.isFile()||e.isDirectory())).sort((a,b)=>a.name.localeCompare(b.name));
 const files=entries.slice(offset,offset+limit).map(e=>({name:(relative?relative+'/':'')+e.name,kind:e.isDirectory()?'directory':'file'}));
 const nextOffset=Math.min(entries.length,offset+files.length);
 return result({kind:'directory',path:target,files,offset,nextOffset,total:entries.length,hasMore:nextOffset<entries.length,truncated:nextOffset<entries.length,message:'使用 files 中的准确相对路径读取文件或继续列出目录。'});
}
export async function artifact(directory,name,args) {
 if(!artifactTools.some(t=>t.name===name))throw new Error('未知工件操作');
 await fs.mkdir(directory,{recursive:true});
 if((await fs.lstat(directory)).isSymbolicLink())throw new Error('工件根目录不能是符号链接或目录联接');
 const root=await fs.realpath(directory);
 const isList=name==='TAI_ListArtifacts',isWrite=name==='TAI_WriteArtifact',isEdit=name==='TAI_EditArtifact';
 const {target,relative}=await resolve(root,isList?(args.path??''):args.name,isWrite,isList);
 if(isList)return listing(target,relative,args);
 if(isWrite){
  if(!extensions.test(relative))throw new Error('仅支持文本工件扩展名');
  const bytes=encode(args.content,{encoding:'utf-8',bom:Buffer.alloc(0)});
  try{await fs.writeFile(target,bytes,{flag:'wx'});}catch(error){if(error.code==='EEXIST')throw new Error('工件已存在；请先用 TAI_ReadArtifact 或 TAI_InspectArtifact 获取 SHA256，再用 TAI_EditArtifact 修改，不要更换名称反复创建副本。');throw error;}
  return result({path:target,bytes:bytes.length,sha256:hash(bytes),imported:false});
 }
 const stat=await fs.lstat(target);
 if(stat.isDirectory()){
  if(isEdit)throw new Error('编辑目标是目录，请先查询并选择准确文件');
  return listing(target,relative,{offset:args.offset});
 }
 if(!stat.isFile()||stat.isSymbolicLink())throw new Error('只允许读取普通工件文件');
 if(!extensions.test(relative))throw new Error('仅支持文本工件扩展名');
 if(stat.size>MAX)throw new Error('工件超过 64 MB 上限');
 const bytes=await fs.readFile(target),sha256=hash(bytes),decoded=decode(bytes);
 if(args.expectedSha256!==undefined&&(typeof args.expectedSha256!=='string'||args.expectedSha256.toLowerCase()!==sha256))throw new Error('工件 SHA256 已变化，请重新读取后编辑或分页');
 if(name==='TAI_InspectArtifact')return result({...inspectText(decoded.text,relative),path:target,sha256,bytes:bytes.length,encoding:decoded.encoding});
 if(name==='TAI_SearchArtifact')return result({...searchText(decoded.text,args),path:target,sha256,encoding:decoded.encoding});
 if(isEdit){
  if(typeof args.expectedSha256!=='string')throw new Error('缺少有效 SHA256，请重新读取后编辑');
  const modes=[args.content!==undefined,args.edits!==undefined,args.oldText!==undefined||args.newText!==undefined];
  if(modes.filter(Boolean).length!==1)throw new Error('content、edits、oldText/newText 必须且只能提供一种编辑方式');
  if(args.replaceAll!==undefined&&(!modes[2]||typeof args.replaceAll!=='boolean'))throw new Error('replaceAll 仅用于单次文本替换且必须是布尔值');
  let updated=decoded.text;
  if(modes[0])updated=args.content;
  else {
   const edits=modes[1]?args.edits:[args];
   if(!Array.isArray(edits)||edits.length<1||edits.length>1000)throw new Error('edits 必须包含1–1000个替换操作');
   for(const edit of edits){
    if(!edit||typeof edit.oldText!=='string'||!edit.oldText||typeof edit.newText!=='string'||(edit.replaceAll!==undefined&&typeof edit.replaceAll!=='boolean'))throw new Error('必须提供非空旧文本和替换文本');
    const at=updated.indexOf(edit.oldText);
    if(at<0)throw new Error('旧文本没有匹配，整批编辑未写入');
    if(!edit.replaceAll&&updated.indexOf(edit.oldText,at+edit.oldText.length)>=0)throw new Error('旧文本必须唯一匹配，请提供更多上下文或显式 replaceAll');
    updated=edit.replaceAll?updated.split(edit.oldText).join(edit.newText):updated.slice(0,at)+edit.newText+updated.slice(at+edit.oldText.length);
    if(updated.length>MAX)throw new Error('修改后的工件超过 64 MB');
   }
  }
  const output=encode(updated,decoded);
  const backupDirectory=path.join(root,'artifact-history');
  await fs.mkdir(backupDirectory,{recursive:true});
  if((await fs.lstat(backupDirectory)).isSymbolicLink())throw new Error('备份目录不能是符号链接');
  const backupPath=path.join(backupDirectory,randomUUID()+path.extname(target));
  await fs.writeFile(backupPath,bytes,{flag:'wx'});
  const temp=target+'.'+randomUUID()+'.tmp';
  try{
   await fs.writeFile(temp,output,{flag:'wx'});
   await resolve(root,relative);
   if(hash(await fs.readFile(target))!==sha256)throw new Error('工件在编辑期间已变化，请重新读取');
   await fs.rename(temp,target);
  }finally{await fs.rm(temp,{force:true});}
  return result({path:target,backupPath,sha256:hash(output),bytes:output.length,encoding:decoded.encoding,imported:false});
 }
 let offset=args.offset??0;const maxChars=args.maxChars??20000,text=decoded.text;
 if(!Number.isSafeInteger(offset)||offset<0||offset>text.length||!Number.isSafeInteger(maxChars)||maxChars<1||maxChars>20000)throw new Error('offset 必须在文件字符范围内，maxChars 必须为 1–20000');
 if(offset>0&&/[\uDC00-\uDFFF]/.test(text[offset])&&/[\uD800-\uDBFF]/.test(text[offset-1]))offset--;
 let end=Math.min(text.length,offset+maxChars);
 if(end<text.length&&/[\uD800-\uDBFF]/.test(text[end-1])&&/[\uDC00-\uDFFF]/.test(text[end]))end++;
 return {content:[{type:'text',text:text.slice(offset,end)},{type:'text',text:JSON.stringify({path:target,sha256,bytes:bytes.length,encoding:decoded.encoding,offset,totalChars:text.length,nextOffset:end,hasMore:end<text.length})}]};
}
