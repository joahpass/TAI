import fs from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { checkedResult, resultData } from './results.mjs';
const imports=new Set(['ImportBlock','ImportType','ImportTypes','ImportFromDocuments','ImportExternalSource']);
const edits=new Set(['DeleteBlock','RenameBlock']);
const hmiEdits=new Set(['SetHmiObjectAttributes','ImportHmiObject']);
const exactPath=value=>value.replaceAll('\\','/').replace(/\/+$/,'');
const normalize=value=>value.replaceAll('\\','/').replace(/\/+$/,'').toLowerCase();

export class EngineeringExecution {
  constructor({directory,call,signal,emit}) {
    Object.assign(this,{directory,call,signal,emit});
    this.id=randomUUID();this.dirty=new Set();this.failed=new Set();
  }
  async identity() {
    this.signal.throwIfAborted();
    const response=checkedResult(await this.call('GetState',{}));
    const state=resultData(response);
    const connected=state?.IsConnected??state?.isConnected;
    const projectPath=state?.ProjectPath??state?.projectPath;
    if(response.isError || connected!==true || typeof projectPath!=='string' || !projectPath.trim())
      throw new Error('无法核实当前工程路径；请连接工程并使用更新后的 TAI MCP。');
    return normalize(projectPath);
  }
  async ensureProject(project) {
    if(await this.identity()!==project) throw new Error('执行期间工程已切换，停止后续操作；原工程编译状态仍待核实。');
  }
  async invoke(name,args) {
    this.signal.throwIfAborted();
    if(!this.loaded) {
      try {const state=JSON.parse(await fs.readFile(path.join(this.directory,'execution-state.json'),'utf8'));this.dirty=new Set(state.dirty);this.failed=new Set(state.failed);}
      catch(error){if(error.code!=='ENOENT')throw error;}
      this.loaded=true;
    }
    if(name==='ImportBlocksFromDocuments') throw new Error('批量文档接口不能确认逐文件导入结果，请使用 ImportFromDocuments 逐个导入并核实。');
    const saving=['SaveProject','SaveAsProject'].includes(name);
    if(!saving && name!=='CompileSoftware' && !imports.has(name) && !edits.has(name) && !hmiEdits.has(name)) return checkedResult(await this.call(name,args));
    const project=await this.identity();
    if(saving) {
      const pending=[...new Set([...this.dirty,...this.failed])].filter(key=>{try{return JSON.parse(key)[0]===project;}catch{return true;}});
      if(pending.length) throw new Error('仍有未通过编译的软件，已阻止保存。请修复并使用记录中的软件路径编译成功：'+pending.join(', '));
      return checkedResult(await this.call(name,args));
    }
    if(typeof args.softwarePath!=='string'||!args.softwarePath.trim())throw new Error('必须指定已核实的软件路径');
    const hmiKey=JSON.stringify([project,exactPath(args.softwarePath),'hmi']);
    const key=hmiEdits.has(name)||this.dirty.has(hmiKey)||this.failed.has(hmiKey)?hmiKey:JSON.stringify([project,normalize(args.softwarePath)]);
    if(name==='CompileSoftware') {
      this.dirty.add(key);await this.save();
      const result=checkedResult(await this.call(name,args));
      this.verifyCompile(result);
      await this.ensureProject(project);
      await this.markCompile(key,result);
      return result;
    }
    const backupPath=path.join(this.directory,'backups',this.id,randomUUID());
    await fs.mkdir(backupPath,{recursive:true});
    if (name==='ImportExternalSource') {
      // A source can generate both blocks and PLC data types. Verify both inventories before mutation.
      const typeBackup=await this.automatic('ExportTypes',{softwarePath:args.softwarePath,exportPath:path.join(backupPath,'types'),regexName:'',preservePath:true});
      const typeData=resultData(typeBackup),typeMeta=typeData?.Meta??typeData?.meta;
      if(typeBackup.isError||typeMeta?.success!==true||Number(typeMeta.inconsistentTypes??0)>0||
        !Number.isInteger(typeMeta.totalTypes)||typeMeta.totalTypes!==typeMeta.exportedTypes)
        throw new Error('外部源生成前的数据类型备份未完整成功，已阻止生成。');
      await this.ensureProject(project);
    }
    if (!edits.has(name) && !hmiEdits.has(name)) {
      const typeImport=name.includes('Type');
      const backup=await this.automatic(typeImport?'ExportTypes':'BackupSoftwareForEdit',typeImport?{softwarePath:args.softwarePath,exportPath:backupPath,regexName:'',preservePath:true}:{softwarePath:args.softwarePath,backupPath});
      const data=resultData(backup),meta=data?.Meta??data?.meta;
      if(backup.isError||meta?.success!==true||
        (typeImport ? Number(meta.inconsistentTypes??0)>0 || meta.totalTypes!==meta.exportedTypes : data?.complete!==true || (data?.Succeeded??data?.succeeded)!==true || data?.backupScope!=='software-blocks'))
        throw new Error(`导入前备份未完整成功，已阻止导入。备份目录：${backupPath}。具体原因：${JSON.stringify(data?.blockers??data?.Message??meta)}`);
    }
    await this.ensureProject(project);
    this.dirty.add(key);await this.save();
    const ownedBackup=hmiEdits.has(name)?path.join(backupPath,'object.xml'):backupPath;
    const result=checkedResult(await this.call(name,(edits.has(name)||hmiEdits.has(name))?{...args,backupPath:ownedBackup}:args));
    if(edits.has(name) && resultData(result)?.postconditionVerified!==true) result.isError=true;
    if(hmiEdits.has(name)) {
      const data=resultData(result),meta=data?.Meta??data?.meta;
      if((data?.Succeeded??data?.succeeded)!==true||meta?.success!==true||data?.postconditionVerified!==true||data?.backupPath!==ownedBackup||!/^[a-f0-9]{64}$/i.test(data?.sha256??'')) result.isError=true;
    }
    if(result.isError)return result;
    await this.ensureProject(project);
    const compile=await this.automatic('CompileSoftware',{softwarePath:args.softwarePath});
    await this.ensureProject(project);
    await this.markCompile(key,compile);
    return {...result,content:[...(result.content??[]),{type:'text',text:JSON.stringify({TAI:{backupPath,compileVerified:!compile.isError,compile:resultData(compile)}})}],isError:Boolean(compile.isError)};
  }
  async save() {
    await fs.mkdir(this.directory,{recursive:true});
    const target=path.join(this.directory,'execution-state.json');
    await fs.writeFile(target+'.tmp',JSON.stringify({version:2,dirty:[...this.dirty],failed:[...this.failed]}));
    await fs.rename(target+'.tmp',target);
  }
  verifyCompile(result) {if((resultData(result)?.Succeeded??resultData(result)?.succeeded)!==true)result.isError=true;}
  async markCompile(key,result) {
    if(result.isError)this.failed.add(key);else{this.dirty.delete(key);this.failed.delete(key);}
    await this.save();
  }
  async automatic(name,args) {
    this.signal.throwIfAborted();
    const id=`automatic-${randomUUID()}`;
    this.emit({type:'tool_start',id,name,arguments:args});
    let result;
    try{result=checkedResult(await this.call(name,args));}catch(error){result={isError:true,content:[{type:'text',text:error.message}]};}
    if(name==='CompileSoftware')this.verifyCompile(result);
    this.emit({type:'tool_end',id,name,arguments:args,result,isError:Boolean(result.isError)});
    return result;
  }
}
