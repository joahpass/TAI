import path from 'node:path';
import { resolveChatSelection } from './chat-options.mjs';
import { verifyProjectContext, normalizeProjectPath } from './project-context.mjs';
import { completion } from './model.mjs';
import { defaultSystemPrompt } from './prompts.mjs';
import { EngineeringExecution } from './execution.mjs';
const readNames = new Set(['TAI_ReadArtifact','TAI_ListArtifacts','TAI_InspectArtifact','TAI_SearchArtifact','GetCompatibilityInfo','GetState','GetProject','GetProjectTree','GetDeviceInfo','GetDeviceItemInfo','GetDevices','GetSoftwareInfo','GetSoftwareTree','GetBlockInfo','GetBlocks','GetBlocksWithHierarchy','GetTypeInfo','GetTypes','GetHmiEditableObject','GetHmiSoftware','GetHmiScreens','GetHmiTagTables','GetHmiConnections','GetHmiScreenContent']);
const writeNames = new Set(['SetHmiObjectAttributes','ExportHmiObject','ImportHmiObject','CompileSoftware','SaveProject','SaveAsProject','ExportBlock','ImportBlock','ExportBlocks','ExportType','ImportType','ExportTypes','ExportAsDocuments','ExportBlocksAsDocuments','ImportFromDocuments','ImportExternalSource','DeleteBlock','RenameBlock','BackupSoftwareForEdit']);
const lifecycle = new Set(['Connect','Disconnect','OpenProject','CloseProject']);
export const permissionModes = [
  {id:'read',label:'只读',description:'读取工程信息和本地工件；工程生命周期操作仍通过手动入口。'},
  {id:'artifact',label:'工作区内修改',description:'读取工程信息，创建和修改本地 TAI 工件；不写入 TIA 工程。'},
  {id:'write',label:'完全权限',description:'允许 TAI 已提供的工程写入工具和本地工件读写；打开工程限于工作区绑定路径。'}
];
const artifactWrites=new Set(['TAI_WriteArtifact','TAI_EditArtifact']);
export function effect(name) { return readNames.has(name) ? 'read' : artifactWrites.has(name) ? 'artifact' : lifecycle.has(name) ? 'lifecycle' : 'write'; }
export function permitted(name, mode) { return ['read','artifact','write'].includes(mode) && (readNames.has(name) || (artifactWrites.has(name) && ['artifact','write'].includes(mode)) || (mode === 'write' && (writeNames.has(name)||lifecycle.has(name)))); }
export { artifactTools, artifact } from './artifacts.mjs';
import { artifactTools, artifact } from './artifacts.mjs';
export async function runChat({store,mcp,conversation,project,message,mode,emit,signal,artifactDirectory,model,reasoningEffort,configSnapshot,modelCompletion=completion}) {
  project = project ? structuredClone(project) : null;
  if(!['read','artifact','write'].includes(mode))throw new Error('无效的工作模式');
  const config = resolveChatSelection(configSnapshot??store.config,{model,reasoningEffort});
  const history = conversation.messages.slice(-30).map(m=>({role:m.role,content:m.content}));
  conversation.messages.push({role:'user',content:message,mode,model:config.model,provider:config.provider,reasoningEffort:config.reasoningEffort,createdAt:new Date().toISOString()});
  if (conversation.messages.length===1 && ['新对话','新设计'].includes(conversation.title)) conversation.title=message.slice(0,40);
  conversation.updatedAt=new Date().toISOString(); conversation.revision=(conversation.revision??0)+1; await store.save('conversations');
  const calls=[]; let answer='';
  const execution=new EngineeringExecution({directory:artifactDirectory,signal,call:async(name,args)=>{
    if(!permitted(name,mode))throw new Error('当前模式不允许此工具');
    if(name==='OpenProject') {
      if(typeof args.path!=='string'||!path.win32.isAbsolute(args.path)||/[\x00-\x1f]/u.test(args.path)||Object.keys(args).some(k=>k!=='path')||!normalizeProjectPath(project?.tiaProjectPath)||normalizeProjectPath(args.path)!==normalizeProjectPath(project.tiaProjectPath))throw new Error('OpenProject 路径必须匹配当前工作区绑定工程');
    } else if(!['Connect','Disconnect'].includes(name) && (project||effect(name)!=='read'))await verifyProjectContext(mcp,project);
    if(!permitted(name,mode))throw new Error('当前模式不允许此工具');
    return mcp.call(name,args);
  },emit:event=>{
    if(event.type==='tool_end') calls.push({id:event.id,name:event.name,arguments:event.arguments,result:event.result,isError:event.isError,automatic:true});
    emit(event);
  }});
  try {
    let tools=[];
    if (mcp.ready) tools=mcp.tools.filter(t=>permitted(t.name,mode));
    tools.push(...artifactTools.filter(t=>permitted(t.name,mode)));
    const messages=[{role:'system',content:config.systemPrompt || defaultSystemPrompt},{role:'system',content:`项目上下文：${JSON.stringify(project ? {id:project.id,name:project.name,description:project.description,instructions:project.instructions ?? '',tiaProjectPath:project.tiaProjectPath} : null)}。运行上下文：模式 ${mode}；TIA 已连接 ${mcp.connected}；工件目录 ${artifactDirectory}。工具由运行时提供。连接和工程生命周期工具仅完全权限模式提供；打开工程路径须匹配工作区绑定。旧工具记录已省略，必要时查询当前状态。`},...history,{role:'user',content:message}];
    for(let step=0;;step++) {
      signal.throwIfAborted(); emit({type:'status',message:`模型处理中 · 第 ${step+1} 轮`});
      const response=await modelCompletion(config,messages,tools,signal);
      if(response.content) { answer+=(answer?'\n':'')+response.content; emit({type:'text',text:response.content}); }
      if(!response.tool_calls?.length) break;
      messages.push(response);
      for(const call of response.tool_calls) {
        signal.throwIfAborted(); const name=call.function.name; let args,result;
        try {
          args=JSON.parse(call.function.arguments || '{}');
          if(!permitted(name,mode)||!tools.some(t=>t.name===name)) throw new Error('当前模式不允许此工具');
          emit({type:'tool_start',id:call.id,name,arguments:args});
          result=name.startsWith('TAI_') ? await artifact(artifactDirectory,name,args) : await execution.invoke(name,args);
        } catch(error) { result={isError:true,content:[{type:'text',text:error.message}]}; }
        calls.push({id:call.id,name,arguments:args,result,isError:Boolean(result.isError)});
        emit({type:'tool_end',...calls.at(-1)});
        messages.push({role:'tool',tool_call_id:call.id,content:JSON.stringify(result).slice(0,100000)});
      }
    }
  } catch(error) {
    const text=signal.aborted?'任务已取消；已开始的博途操作可能已经完成，请刷新工程状态。':error.message;
    answer+=(answer?'\n':'')+text; emit({type:'error',error:text});
  } finally {
    conversation.messages.push({role:'assistant',content:answer || '模型未返回文本，请查看工具执行结果。',tools:calls,createdAt:new Date().toISOString()});
    conversation.updatedAt=new Date().toISOString(); conversation.revision=(conversation.revision??0)+1; await store.save('conversations');
  }
  emit({type:'done',conversationId:conversation.id});
}
